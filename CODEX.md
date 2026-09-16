# FORTIQ development rules

- Implement milestones in order and stop at the requested milestone.
- Every machine runs the same `fortiq-service` binary.
- Node mode is derived only from the presence of `authorization.operator_peer_id`.
- Never print, log, transmit, or commit identity private keys.
- Keep network inputs bounded and avoid unsafe Rust.
- Before claiming completion, run build, tests, formatting, and Clippy for the workspace.

