use anyhow::Result;
use std::path::PathBuf;

pub const SERVICE_NAME: &str = "FortiqService";
pub const SERVICE_DISPLAY_NAME: &str = "FORTIQ Sovereign Remote Administration Node";
pub const SERVICE_DESCRIPTION: &str =
    "Peer service providing sovereign remote support and administration via encrypted P2P channels.";

pub fn install(config: Option<PathBuf>) -> Result<()> {
    #[cfg(windows)]
    return windows::install_service(config.as_deref());
    #[cfg(unix)]
    return unix::install_service(config.as_deref());
    #[cfg(not(any(windows, unix)))]
    anyhow::bail!("Service installation not supported on this platform");
}

pub fn uninstall() -> Result<()> {
    #[cfg(windows)]
    return windows::uninstall_service();
    #[cfg(unix)]
    return unix::uninstall_service();
    #[cfg(not(any(windows, unix)))]
    anyhow::bail!("Service uninstallation not supported on this platform");
}

pub fn start() -> Result<()> {
    #[cfg(windows)]
    return windows::start_service();
    #[cfg(unix)]
    return unix::start_service();
    #[cfg(not(any(windows, unix)))]
    anyhow::bail!("Service start not supported on this platform");
}

pub fn stop() -> Result<()> {
    #[cfg(windows)]
    return windows::stop_service();
    #[cfg(unix)]
    return unix::stop_service();
    #[cfg(not(any(windows, unix)))]
    anyhow::bail!("Service stop not supported on this platform");
}

pub fn status() -> Result<()> {
    #[cfg(windows)]
    return windows::status_service();
    #[cfg(unix)]
    return unix::status_service();
    #[cfg(not(any(windows, unix)))]
    anyhow::bail!("Service status not supported on this platform");
}

#[cfg(windows)]
pub mod windows {
    use anyhow::{Context, Result};
    use std::{
        ffi::OsString,
        path::{Path, PathBuf},
        sync::mpsc,
        time::Duration,
    };
    use windows_service::{
        define_windows_service,
        service::{
            ServiceAccess, ServiceControl, ServiceControlAccept, ServiceErrorControl,
            ServiceExitCode, ServiceStartType, ServiceState, ServiceStatus, ServiceType,
        },
        service_control_handler::{self, ServiceControlHandlerResult},
        service_dispatcher,
        service_manager::{ServiceManager, ServiceManagerAccess},
    };

    use super::{SERVICE_DESCRIPTION, SERVICE_DISPLAY_NAME, SERVICE_NAME};

    pub fn install_service(config_path: Option<&Path>) -> Result<()> {
        let exe_path = std::env::current_exe().context("Failed to get current executable path")?;
        let config_arg = match config_path {
            Some(p) => p.to_path_buf(),
            None => fortiq_core::Config::system_service_default_path(),
        };

        let manager = ServiceManager::local_computer(
            None::<&str>,
            ServiceManagerAccess::CONNECT | ServiceManagerAccess::CREATE_SERVICE,
        )
        .context("Failed to connect to Windows Service Manager (Administrator rights required)")?;

        let service_info = windows_service::service::ServiceInfo {
            name: OsString::from(SERVICE_NAME),
            display_name: OsString::from(SERVICE_DISPLAY_NAME),
            service_type: ServiceType::OWN_PROCESS,
            start_type: ServiceStartType::AutoStart,
            error_control: ServiceErrorControl::Normal,
            executable_path: exe_path,
            launch_arguments: vec![
                OsString::from("--service-run"),
                OsString::from("--config"),
                config_arg.clone().into_os_string(),
            ],
            dependencies: vec![],
            account_name: None,
            account_password: None,
        };

        let service = manager
            .create_service(&service_info, ServiceAccess::CHANGE_CONFIG)
            .context("Failed to create Windows service")?;

        service
            .set_description(SERVICE_DESCRIPTION)
            .context("Failed to set service description")?;

        println!("Service '{SERVICE_NAME}' successfully installed.");
        println!("Display Name: {SERVICE_DISPLAY_NAME}");
        println!("Config:       {}", config_arg.display());
        println!("Start Type:   Automatic");
        println!("\nYou can start the service with: fortiq-service service start");
        Ok(())
    }

    pub fn uninstall_service() -> Result<()> {
        let manager = ServiceManager::local_computer(None::<&str>, ServiceManagerAccess::CONNECT)
            .context(
            "Failed to connect to Windows Service Manager (Administrator rights required)",
        )?;

        let service = manager
            .open_service(
                SERVICE_NAME,
                ServiceAccess::STOP | ServiceAccess::DELETE | ServiceAccess::QUERY_STATUS,
            )
            .context("Service is not installed or access denied")?;

        if let Ok(status) = service.query_status() {
            if status.current_state != ServiceState::Stopped {
                let _ = service.stop();
                std::thread::sleep(Duration::from_millis(500));
            }
        }

        service.delete().context("Failed to delete service")?;
        println!("Service '{SERVICE_NAME}' successfully uninstalled.");
        Ok(())
    }

    pub fn start_service() -> Result<()> {
        let manager = ServiceManager::local_computer(None::<&str>, ServiceManagerAccess::CONNECT)
            .context("Failed to connect to Windows Service Manager")?;

        let service = manager
            .open_service(SERVICE_NAME, ServiceAccess::START)
            .context("Failed to open service for start")?;

        service
            .start(&[] as &[&str])
            .context("Failed to start service")?;
        println!("Start request sent to service '{SERVICE_NAME}'.");
        Ok(())
    }

    pub fn stop_service() -> Result<()> {
        let manager = ServiceManager::local_computer(None::<&str>, ServiceManagerAccess::CONNECT)
            .context("Failed to connect to Windows Service Manager")?;

        let service = manager
            .open_service(SERVICE_NAME, ServiceAccess::STOP)
            .context("Failed to open service for stop")?;

        service.stop().context("Failed to stop service")?;
        println!("Stop request sent to service '{SERVICE_NAME}'.");
        Ok(())
    }

    pub fn status_service() -> Result<()> {
        let manager = ServiceManager::local_computer(None::<&str>, ServiceManagerAccess::CONNECT)
            .context("Failed to connect to Windows Service Manager")?;

        let service = manager
            .open_service(SERVICE_NAME, ServiceAccess::QUERY_STATUS)
            .context(format!(
                "Service '{SERVICE_NAME}' is not installed or access denied"
            ))?;

        let status = service
            .query_status()
            .context("Failed to query service status")?;
        let state_str = match status.current_state {
            ServiceState::Stopped => "STOPPED",
            ServiceState::StartPending => "START_PENDING",
            ServiceState::StopPending => "STOP_PENDING",
            ServiceState::Running => "RUNNING",
            ServiceState::ContinuePending => "CONTINUE_PENDING",
            ServiceState::PausePending => "PAUSE_PENDING",
            ServiceState::Paused => "PAUSED",
        };
        println!("Service '{SERVICE_NAME}': {state_str}");
        Ok(())
    }

    define_windows_service!(ffi_service_main, my_service_main);

    static CONFIG_PATH: std::sync::OnceLock<PathBuf> = std::sync::OnceLock::new();

    pub fn run_service_dispatcher(config_path: PathBuf) -> Result<()> {
        let _ = CONFIG_PATH.set(config_path);
        service_dispatcher::start(SERVICE_NAME, ffi_service_main)
            .context("Failed to start Windows service dispatcher")?;
        Ok(())
    }

    fn my_service_main(_arguments: Vec<OsString>) {
        if let Err(e) = run_service() {
            eprintln!("Service failed: {e:?}");
        }
    }

    fn run_service() -> Result<()> {
        let (shutdown_tx, shutdown_rx) = mpsc::channel();

        let event_handler = move |control_event| -> ServiceControlHandlerResult {
            match control_event {
                ServiceControl::Stop | ServiceControl::Shutdown => {
                    let _ = shutdown_tx.send(());
                    ServiceControlHandlerResult::NoError
                }
                ServiceControl::Interrogate => ServiceControlHandlerResult::NoError,
                _ => ServiceControlHandlerResult::NotImplemented,
            }
        };

        let status_handle = service_control_handler::register(SERVICE_NAME, event_handler)
            .context("Failed to register service control handler")?;

        status_handle
            .set_service_status(ServiceStatus {
                service_type: ServiceType::OWN_PROCESS,
                current_state: ServiceState::Running,
                controls_accepted: ServiceControlAccept::STOP | ServiceControlAccept::SHUTDOWN,
                exit_code: ServiceExitCode::Win32(0),
                checkpoint: 0,
                wait_hint: Duration::default(),
                process_id: None,
            })
            .context("Failed to set service status to RUNNING")?;

        let config_path = CONFIG_PATH
            .get()
            .cloned()
            .unwrap_or_else(fortiq_core::Config::resolve_default_path);

        let rt = tokio::runtime::Builder::new_multi_thread()
            .enable_all()
            .build()
            .context("Failed to build Tokio runtime for service")?;

        rt.block_on(async {
            let (stop_node_tx, stop_node_rx) = tokio::sync::oneshot::channel::<()>();

            std::thread::spawn(move || {
                let _ = shutdown_rx.recv();
                let _ = stop_node_tx.send(());
            });

            tokio::select! {
                res = crate::run_daemon(config_path) => {
                    if let Err(e) = res {
                        eprintln!("Daemon execution error: {e:?}");
                    }
                }
                _ = stop_node_rx => {
                    println!("Service stop signal received, shutting down daemon cleanly.");
                }
            }
        });

        status_handle
            .set_service_status(ServiceStatus {
                service_type: ServiceType::OWN_PROCESS,
                current_state: ServiceState::Stopped,
                controls_accepted: ServiceControlAccept::empty(),
                exit_code: ServiceExitCode::Win32(0),
                checkpoint: 0,
                wait_hint: Duration::default(),
                process_id: None,
            })
            .context("Failed to set service status to STOPPED")?;

        Ok(())
    }
}

#[cfg(unix)]
pub mod unix {
    use super::SERVICE_DESCRIPTION;
    use anyhow::{Context, Result};
    use std::{path::Path, process::Command};

    const SYSTEMD_UNIT_PATH: &str = "/etc/systemd/system/fortiq.service";

    pub fn install_service(config_path: Option<&Path>) -> Result<()> {
        let exe_path = std::env::current_exe().context("Failed to get current executable path")?;
        let config_arg = match config_path {
            Some(p) => p.to_path_buf(),
            None => fortiq_core::Config::system_service_default_path(),
        };

        let unit_content = format!(
            "[Unit]\n\
Description={SERVICE_DESCRIPTION}\n\
After=network.target network-online.target\n\
Wants=network-online.target\n\n\
[Service]\n\
Type=simple\n\
ExecStart={} --config {}\n\
Restart=always\n\
RestartSec=5s\n\
LimitNOFILE=65536\n\
StandardOutput=journal\n\
StandardError=journal\n\
RuntimeDirectory=fortiq\n\
RuntimeDirectoryMode=0770\n\n\
[Install]\n\
WantedBy=multi-user.target\n",
            exe_path.display(),
            config_arg.display()
        );

        std::fs::write(SYSTEMD_UNIT_PATH, unit_content)
            .with_context(|| format!("Failed to write systemd unit file to {SYSTEMD_UNIT_PATH} (root permissions required)"))?;

        let _ = Command::new("systemctl").arg("daemon-reload").status();
        let _ = Command::new("systemctl")
            .args(["enable", "fortiq"])
            .status();

        println!("systemd unit installed at {SYSTEMD_UNIT_PATH}");
        println!(
            "Service enabled. Start with: fortiq-service service start (or systemctl start fortiq)"
        );
        Ok(())
    }

    pub fn uninstall_service() -> Result<()> {
        let _ = Command::new("systemctl")
            .args(["disable", "--now", "fortiq"])
            .status();
        if std::path::Path::new(SYSTEMD_UNIT_PATH).exists() {
            std::fs::remove_file(SYSTEMD_UNIT_PATH)
                .with_context(|| format!("Failed to remove {SYSTEMD_UNIT_PATH}"))?;
        }
        let _ = Command::new("systemctl").arg("daemon-reload").status();
        println!("Service uninstalled and systemd unit removed.");
        Ok(())
    }

    pub fn start_service() -> Result<()> {
        let status = Command::new("systemctl")
            .args(["start", "fortiq"])
            .status()
            .context("Failed to invoke systemctl start fortiq")?;
        if status.success() {
            println!("Service 'fortiq' started.");
            Ok(())
        } else {
            anyhow::bail!("systemctl start fortiq failed with exit status: {status}");
        }
    }

    pub fn stop_service() -> Result<()> {
        let status = Command::new("systemctl")
            .args(["stop", "fortiq"])
            .status()
            .context("Failed to invoke systemctl stop fortiq")?;
        if status.success() {
            println!("Service 'fortiq' stopped.");
            Ok(())
        } else {
            anyhow::bail!("systemctl stop fortiq failed with exit status: {status}");
        }
    }

    pub fn status_service() -> Result<()> {
        let status = Command::new("systemctl")
            .args(["status", "fortiq"])
            .status()
            .context("Failed to invoke systemctl status fortiq")?;
        let _ = status;
        Ok(())
    }
}
