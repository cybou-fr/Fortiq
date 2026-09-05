# Restore files with the Fortiq desktop app

Implementation status: implemented for restoring complete source folders or individual files/subfolders from a selected snapshot. Updated September 2026.

## What you need

- The backup repository location and access to that storage.
- The recovery-kit folder containing kit.json and its envelope files.
- The recovery phrase recorded when the repository was created.
- For S3 storage, the access key, secret key and region if required by the endpoint.
- An existing destination parent folder with sufficient free space, outside the original source, repository and recovery kit.
- The pinned engine and password helper included with Fortiq.

The recovery executor does not require a running Fortiq service or a device-bound unlock key. On a new machine, the setup screen offers **Run as Portable**. You can also launch Emergency File Recovery directly from the system tray menu. Installed-mode GUI actions may ask you to reopen Fortiq as administrator through Windows UAC; repeat the action in the new instance.

## Restore a backup

1. Open **Recovery**, then **Restore files from a recovery kit** (or click **Emergency File Recovery...** in the system tray). This action is available even when the local list of protected sources is empty.
2. Enter the backup repository location and select the recovery-kit folder.
3. Enter the recovery phrase. For object storage, expand **S3 credentials** and enter the required credentials.
4. Select **Find backups**. Fortiq checks the engine and kit, opens the repository and verifies its identity before displaying snapshots.
5. Select a backup by date. The selection includes its source path and a shortened snapshot identifier.
6. Choose the restore mode:
   - **Restore all files**: Restores the complete source subtree from the snapshot into the chosen destination parent folder.
   - **Selective file / folder restore**: Click **Explore & search files in snapshot** to browse the file tree or search file names, select specific files or folders, and restore only the chosen items.
7. Choose an existing destination parent folder. Review the full new recovery-folder path shown below it.
8. Select **Restore all files** or **Restore selected items**. Wait for the completion message, then use **Open restored folder**.

Fortiq supports both full snapshot subtree restoration and selective file/folder restoration. It does not overwrite existing destination folders or restore inside the source, repository or recovery-kit directory. Destination ancestors containing symbolic links or junctions are rejected. The engine restores through a private staging directory and checks the restored tree before publishing the destination.

## Cancellation and failures

**Cancel operation** requests cancellation and waits for the executor to stop. Closing a busy recovery window requests cancellation and keeps the window open until the operation ends. A cancelled or failed operation is not shown as completed.

Read the error before retrying. Check storage connectivity, credentials, available disk space and destination permissions. Choose another destination if one already exists. A successful restore reports its output folder and byte count; it does not establish a recovery-proof verdict. Use the separate recovery-proof action to produce that evidence.

Recovery phrases and S3 credentials are held for the current session, not saved as settings or placed in process arguments. Masked input fields are cleared after loading; the session releases access material after success, reset or closing. Managed strings do not provide guaranteed memory zeroization.

## Protection setup limitations

- Portable mode can create a repository and recovery kit but does not run automatic backups. Install Fortiq and configure protection in installed mode for scheduling.
- The current installed default is nightly at 02:30 local time. Custom schedule editing is not available in the GUI.
- Ordinary closing is blocked while protection is being created or the recovery phrase awaits confirmation. This does not protect against process termination, power loss or a crash; resuming an interrupted key ceremony is not implemented.
- Installed provisioning and proof require the service. They do not silently switch to local execution if it is down.
- A service response timeout means the result is unknown, not that the operation stopped. Check the state before repeating repository creation.

## Verification for this change

The implementation was checked with 64 desktop tests and two targeted integration tests: the existing end-to-end recovery test and the new desktop recovery-backend test. The latter creates an encrypted repository without device unlock, restores through the GUI view model and adapter using the recovery phrase, and compares restored files with the test dataset's hashes. The desktop form was also inspected in a running Windows app.

This is not a claim that the entire integration suite, every storage provider, UAC behavior, all DPI settings or screen-reader workflows were verified.
