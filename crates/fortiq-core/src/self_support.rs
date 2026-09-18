use crate::object::EntityId;
use crate::ticket::TicketRecord;

#[derive(Debug, Clone, Default)]
pub struct LoopbackEndpoint;

#[derive(Debug, Clone)]
pub struct ThisDevice {
    pub entity_id: EntityId,
    pub endpoint: LoopbackEndpoint,
}

impl ThisDevice {
    pub fn new(entity_id: EntityId, endpoint: LoopbackEndpoint) -> Self {
        Self { entity_id, endpoint }
    }
}

#[derive(Debug, Clone, Default)]
pub struct LocalDiagnostics {
    pub is_loopback_active: bool,
}

#[derive(Debug, Clone)]
pub struct SelfSupportEngine {
    pub this_device: ThisDevice,
}

impl SelfSupportEngine {
    pub fn new(this_device: ThisDevice) -> Self {
        Self { this_device }
    }

    pub fn collect_diagnostics(&self, _ticket: Option<&TicketRecord>) -> LocalDiagnostics {
        LocalDiagnostics {
            is_loopback_active: true,
        }
    }
}
