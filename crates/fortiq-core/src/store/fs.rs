use anyhow::{bail, Context, Result};
use std::fs::{self, File};
use std::io::BufWriter;
use std::path::{Path, PathBuf};

use super::object_store::ObjectStore;
use crate::object::{ObjectId, SignedObject};

/// Filesystem-backed immutable ObjectStore.
/// Objects are stored content-addressed under `<root>/objects/<xx>/<id>.cbor`.
#[derive(Debug, Clone)]
pub struct FsObjectStore {
    root: PathBuf,
    objects_dir: PathBuf,
    tmp_dir: PathBuf,
}

impl FsObjectStore {
    pub fn open(root: impl AsRef<Path>) -> Result<Self> {
        let root = root.as_ref().to_path_buf();
        let objects_dir = root.join("objects");
        let tmp_dir = root.join("tmp");

        fs::create_dir_all(&objects_dir)
            .with_context(|| format!("failed to create objects directory {}", objects_dir.display()))?;
        fs::create_dir_all(&tmp_dir)
            .with_context(|| format!("failed to create tmp directory {}", tmp_dir.display()))?;

        Ok(Self {
            root,
            objects_dir,
            tmp_dir,
        })
    }

    pub fn root(&self) -> &Path {
        &self.root
    }

    fn object_path(&self, id: &ObjectId) -> PathBuf {
        let hex = id.to_hex();
        let shard = &hex[..2];
        self.objects_dir.join(shard).join(format!("{hex}.cbor"))
    }

    /// Loads all SignedObjects stored in this FsObjectStore into memory.
    pub fn load_all_objects(&self) -> Result<Vec<SignedObject>> {
        let mut objects = Vec::new();
        if !self.objects_dir.exists() {
            return Ok(objects);
        }

        for shard_entry in fs::read_dir(&self.objects_dir)? {
            let shard_entry = shard_entry?;
            if !shard_entry.file_type()?.is_dir() {
                continue;
            }

            for file_entry in fs::read_dir(shard_entry.path())? {
                let file_entry = file_entry?;
                let path = file_entry.path();
                if path.extension().and_then(|e| e.to_str()) != Some("cbor") {
                    continue;
                }

                let bytes = fs::read(&path)
                    .with_context(|| format!("failed to read object file {}", path.display()))?;
                let obj: SignedObject = ciborium::from_reader(bytes.as_slice())
                    .with_context(|| format!("failed to decode object {}", path.display()))?;
                
                obj.verify()
                    .with_context(|| format!("corrupted object in store: {}", path.display()))?;
                objects.push(obj);
            }
        }

        Ok(objects)
    }
}

impl ObjectStore for FsObjectStore {
    fn put(&self, object: &SignedObject) -> Result<ObjectId> {
        object.verify().context("cannot store invalid SignedObject")?;

        let hex = object.id.to_hex();
        let shard_dir = self.objects_dir.join(&hex[..2]);
        let target_path = shard_dir.join(format!("{hex}.cbor"));

        if target_path.exists() {
            return Ok(object.id);
        }

        fs::create_dir_all(&shard_dir)
            .with_context(|| format!("failed to create shard dir {}", shard_dir.display()))?;

        let tmp_path = self.tmp_dir.join(format!("{}.tmp", uuid::Uuid::new_v4().simple()));
        {
            let file = File::create(&tmp_path)
                .with_context(|| format!("failed to create temp file {}", tmp_path.display()))?;
            let mut writer = BufWriter::new(file);
            ciborium::into_writer(object, &mut writer)
                .context("failed to serialize SignedObject to CBOR")?;
        }

        fs::rename(&tmp_path, &target_path).with_context(|| {
            format!(
                "failed to move temp file {} to {}",
                tmp_path.display(),
                target_path.display()
            )
        })?;

        Ok(object.id)
    }

    fn get(&self, id: &ObjectId) -> Result<Option<SignedObject>> {
        let path = self.object_path(id);
        if !path.exists() {
            return Ok(None);
        }

        let bytes = fs::read(&path)
            .with_context(|| format!("failed to read object {}", path.display()))?;
        let obj: SignedObject = ciborium::from_reader(bytes.as_slice())
            .with_context(|| format!("failed to decode object {}", path.display()))?;

        if obj.id != *id {
            bail!("object file {} ID mismatch: expected {}", path.display(), id);
        }
        obj.verify().context("corrupted object signature in store")?;

        Ok(Some(obj))
    }

    fn has(&self, id: &ObjectId) -> Result<bool> {
        Ok(self.object_path(id).exists())
    }

    fn list(&self) -> Result<Vec<ObjectId>> {
        let mut ids = Vec::new();
        if !self.objects_dir.exists() {
            return Ok(ids);
        }

        for shard_entry in fs::read_dir(&self.objects_dir)? {
            let shard_entry = shard_entry?;
            if !shard_entry.file_type()?.is_dir() {
                continue;
            }

            for file_entry in fs::read_dir(shard_entry.path())? {
                let file_entry = file_entry?;
                let path = file_entry.path();
                if let Some(stem) = path.file_stem().and_then(|s| s.to_str()) {
                    if let Ok(id) = ObjectId::from_hex(stem) {
                        ids.push(id);
                    }
                }
            }
        }

        Ok(ids)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::object::Ed25519Signer;
    use tempfile::tempdir;

    #[test]
    fn test_fs_object_store_roundtrip() {
        let dir = tempdir().unwrap();
        let store = FsObjectStore::open(dir.path()).unwrap();

        let signer = Ed25519Signer::generate();
        let obj = SignedObject::sign(&signer, b"test-payload".to_vec(), 1_000);

        let id = store.put(&obj).unwrap();
        assert_eq!(id, obj.id);
        assert!(store.has(&id).unwrap());

        let retrieved = store.get(&id).unwrap().expect("object should exist");
        assert_eq!(retrieved, obj);

        let all = store.load_all_objects().unwrap();
        assert_eq!(all.len(), 1);
        assert_eq!(all[0].id, id);
    }
}
