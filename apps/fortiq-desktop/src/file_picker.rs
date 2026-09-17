use std::path::PathBuf;

pub trait FilePicker: Send + Sync {
    fn pick_file(&self) -> Option<PathBuf>;
    fn pick_files(&self) -> Vec<PathBuf>;
    fn save_file(&self, default_name: &str) -> Option<PathBuf>;
}

#[derive(Default, Debug, Clone)]
pub struct NativeFilePicker;

impl FilePicker for NativeFilePicker {
    fn pick_file(&self) -> Option<PathBuf> {
        rfd::FileDialog::new().pick_file()
    }

    fn pick_files(&self) -> Vec<PathBuf> {
        rfd::FileDialog::new().pick_files().unwrap_or_default()
    }

    fn save_file(&self, default_name: &str) -> Option<PathBuf> {
        rfd::FileDialog::new()
            .set_file_name(default_name)
            .save_file()
    }
}

#[derive(Default, Debug, Clone)]
pub struct MockFilePicker {
    pub files_to_return: std::sync::Arc<std::sync::Mutex<Vec<PathBuf>>>,
}

impl FilePicker for MockFilePicker {
    fn pick_file(&self) -> Option<PathBuf> {
        self.files_to_return.lock().unwrap().pop()
    }

    fn pick_files(&self) -> Vec<PathBuf> {
        let mut list = self.files_to_return.lock().unwrap();
        let ret = list.clone();
        list.clear();
        ret
    }

    fn save_file(&self, _default_name: &str) -> Option<PathBuf> {
        self.files_to_return.lock().unwrap().pop()
    }
}
