use anyhow::Result;

use crate::object::{ObjectId, SignedObject};

/// Storage trait for immutable, content-addressed SignedObjects.
pub trait ObjectStore: Send + Sync {
    /// Persists a SignedObject. If the object already exists, this is an idempotent no-op.
    fn put(&self, object: &SignedObject) -> Result<ObjectId>;

    /// Retrieves a SignedObject by its ObjectId.
    fn get(&self, id: &ObjectId) -> Result<Option<SignedObject>>;

    /// Checks whether an object exists in storage without loading its contents.
    fn has(&self, id: &ObjectId) -> Result<bool>;

    /// Lists all object IDs currently present in storage.
    fn list(&self) -> Result<Vec<ObjectId>>;
}
