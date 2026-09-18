use anyhow::{bail, Context, Result};
use std::path::{Path, PathBuf};
use std::sync::{Arc, RwLock};
use std::time::{SystemTime, UNIX_EPOCH};

use crate::authority::AuthorityPolicy;
use crate::event::{Event, EventGraph, EventPayload};
use crate::object::{Ed25519Signer, ObjectId, SignedObject};
use crate::reducer::{TicketReducer, TicketStateStore};
use crate::store::{FsObjectStore, ObjectStore};
use crate::ticket::{
    AttachmentRecord, ChatMessage, ShellSessionRecord, TicketDetail, TicketEvent, TicketPriority,
    TicketRecord, TicketState,
};

fn current_timestamp() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs()
}

/// Thread-safe reactive Ticket Engine for FORTIQ v5.
/// Coordinates disk storage of immutable signed objects, the in-memory EventGraph,
/// and the purely-reduced TicketStateStore.
#[derive(Clone)]
pub struct TicketEngine {
    store: Arc<FsObjectStore>,
    graph: Arc<RwLock<EventGraph>>,
    state: Arc<RwLock<TicketStateStore>>,
    signer: Arc<Ed25519Signer>,
    policy: Arc<AuthorityPolicy>,
}

impl TicketEngine {
    pub fn try_new(root_path: impl AsRef<Path>) -> Result<Self> {
        let root = root_path.as_ref();
        let key_file = root.join("node.key");
        let signer = if key_file.exists() {
            let bytes = std::fs::read(&key_file)?;
            if bytes.len() >= 32 {
                let mut seed = [0u8; 32];
                seed.copy_from_slice(&bytes[..32]);
                Ed25519Signer::from_bytes(&seed)
            } else {
                let s = Ed25519Signer::generate();
                let _ = std::fs::write(&key_file, s.to_bytes());
                s
            }
        } else {
            let s = Ed25519Signer::generate();
            let _ = std::fs::create_dir_all(root);
            let _ = std::fs::write(&key_file, s.to_bytes());
            s
        };
        let policy = AuthorityPolicy::default();
        Self::open(root, signer, policy)
    }

    pub fn open_with_signer(
        root_path: impl AsRef<Path>,
        signer: Ed25519Signer,
        policy: AuthorityPolicy,
    ) -> Result<Self> {
        Self::open(root_path, signer, policy)
    }

    pub fn new(root_path: impl AsRef<Path>) -> Self {
        Self::try_new(root_path).expect("persistent ticket engine must open")
    }

    pub fn open_in_memory() -> Result<Self> {
        let path =
            std::env::temp_dir().join(format!("fortiq-inmem-{}", uuid::Uuid::new_v4().simple()));
        Self::open(path, Ed25519Signer::generate(), AuthorityPolicy::default())
    }

    pub fn open(
        root_path: impl AsRef<Path>,
        signer: Ed25519Signer,
        policy: AuthorityPolicy,
    ) -> Result<Self> {
        let store = FsObjectStore::open(root_path)?;
        let mut graph = EventGraph::new();

        // 1. Load all immutable signed objects from disk into the EventGraph
        let objects = store.load_all_objects()?;
        for obj in &objects {
            match Event::from_signed_object(obj) {
                Ok(event) => {
                    let _ = graph.insert(event);
                }
                Err(e) => {
                    tracing::warn!("ignoring invalid object {}: {e}", obj.id);
                }
            }
        }

        // 2. Purely reduce the graph to build the initial in-memory state
        let state = TicketReducer::reduce(&graph);

        Ok(Self {
            store: Arc::new(store),
            graph: Arc::new(RwLock::new(graph)),
            state: Arc::new(RwLock::new(state)),
            signer: Arc::new(signer),
            policy: Arc::new(policy),
        })
    }

    pub fn storage_dir(&self) -> PathBuf {
        self.store.root().to_path_buf()
    }

    pub fn signer(&self) -> &Ed25519Signer {
        &self.signer
    }

    pub fn policy(&self) -> &AuthorityPolicy {
        &self.policy
    }

    pub fn current_heads(&self) -> Vec<ObjectId> {
        let graph = self.graph.read().expect("graph lock poisoned");
        graph.heads().to_vec()
    }

    /// Internal helper to append a TicketEvent to the DAG and store it.
    fn append_ticket_event(
        &self,
        ticket_event: TicketEvent,
        timestamp: u64,
    ) -> Result<(Event, SignedObject)> {
        let mut graph = self.graph.write().expect("graph lock poisoned");
        let parents = graph.heads().to_vec();
        let lamport = graph.next_lamport(&parents);

        let (event, signed_obj) = Event::new(
            parents,
            timestamp,
            lamport,
            EventPayload::Ticket(ticket_event),
            &self.signer,
        )?;

        // Persist to disk first (write-ahead immutable object)
        self.store.put(&signed_obj)?;

        // Update in-memory DAG
        graph.insert(event.clone())?;

        // Incrementally update in-memory state
        let mut state = self.state.write().expect("state lock poisoned");
        TicketReducer::apply_event(&mut state, &event)?;

        Ok((event, signed_obj))
    }

    // ==========================================
    // Mutations
    // ==========================================

    pub fn create_ticket(
        &self,
        title: &str,
        description: &str,
        priority: TicketPriority,
        client_peer_id: &str,
    ) -> Result<TicketRecord> {
        let ticket_id = format!("T-{}", &uuid::Uuid::new_v4().simple().to_string()[..8]);
        let now = current_timestamp();

        let event = TicketEvent::Created {
            ticket_id: ticket_id.clone(),
            title: title.to_string(),
            description: description.to_string(),
            priority,
            client_peer_id: client_peer_id.to_string(),
        };

        self.append_ticket_event(event, now)?;

        self.get_ticket(&ticket_id)?
            .context("failed to retrieve newly created ticket")
    }

    pub fn update_ticket_state(
        &self,
        ticket_id: &str,
        new_state: TicketState,
        actor_peer_id: &str,
    ) -> Result<TicketRecord> {
        let current = self.get_ticket(ticket_id)?.context("ticket not found")?;

        if !self
            .policy
            .can_transition_ticket(current.state, new_state, actor_peer_id)
        {
            bail!(
                "unauthorized or invalid transition from {:?} to {:?}",
                current.state,
                new_state
            );
        }

        let now = current_timestamp();
        let event = TicketEvent::StatusChanged {
            ticket_id: ticket_id.to_string(),
            new_state,
            actor_peer_id: actor_peer_id.to_string(),
            reason: None,
        };

        self.append_ticket_event(event, now)?;

        self.get_ticket(ticket_id)?
            .context("failed to retrieve updated ticket")
    }

    pub fn add_chat_message(&self, chat_msg: &ChatMessage) -> Result<()> {
        let now = if chat_msg.created_at == 0 {
            current_timestamp()
        } else {
            chat_msg.created_at
        };

        let event = TicketEvent::ChatMessageAdded {
            ticket_id: chat_msg.ticket_id.clone(),
            message_id: chat_msg.id.clone(),
            sender_peer_id: chat_msg.sender_peer_id.clone(),
            body: chat_msg.body.clone(),
            timestamp: now,
        };

        self.append_ticket_event(event, now)?;
        Ok(())
    }

    pub fn add_attachment(&self, att: &AttachmentRecord) -> Result<()> {
        let now = if att.created_at == 0 {
            current_timestamp()
        } else {
            att.created_at
        };

        let event = TicketEvent::AttachmentAdded {
            ticket_id: att.ticket_id.clone(),
            attachment_id: att.id.clone(),
            sender_peer_id: att.sender_peer_id.clone(),
            filename: att.filename.clone(),
            size_bytes: att.size_bytes,
            sha256: att.sha256.clone(),
            local_path: att.local_path.clone(),
            timestamp: now,
        };

        self.append_ticket_event(event, now)?;
        Ok(())
    }

    pub fn record_event(
        &self,
        ticket_id: &str,
        kind: &str,
        actor_peer_id: &str,
        metadata: Option<&str>,
    ) -> Result<()> {
        let now = current_timestamp();
        let event = TicketEvent::CustomAudit {
            ticket_id: ticket_id.to_string(),
            event_id: uuid::Uuid::new_v4().simple().to_string(),
            kind: kind.to_string(),
            actor_peer_id: actor_peer_id.to_string(),
            metadata: metadata.map(|s| s.to_string()),
            timestamp: now,
        };
        self.append_ticket_event(event, now)?;
        Ok(())
    }

    fn outbox_dir(&self) -> PathBuf {
        self.store.root().join("outbox")
    }

    pub fn remove_outbox(&self, outbox_id: &str) -> Result<()> {
        let path = self.outbox_dir().join(format!("{outbox_id}.json"));
        if path.exists() {
            std::fs::remove_file(path)?;
        }
        Ok(())
    }

    pub fn enqueue_outbox(&self, peer_id: &str, kind: &str, payload: &str) -> Result<String> {
        let dir = self.outbox_dir();
        std::fs::create_dir_all(&dir)?;
        let id = uuid::Uuid::new_v4().simple().to_string();
        let rec = OutboxRecord {
            id: id.clone(),
            peer_id: peer_id.to_string(),
            kind: kind.to_string(),
            payload: payload.to_string(),
            created_at: current_timestamp(),
        };
        let bytes = serde_json::to_vec_pretty(&rec)?;
        let tmp_path = dir.join(format!("{id}.tmp"));
        let final_path = dir.join(format!("{id}.json"));
        std::fs::write(&tmp_path, &bytes)?;
        std::fs::rename(tmp_path, final_path)?;
        Ok(id)
    }

    pub fn list_outbox_for_peer(&self, peer_id: &str) -> Result<Vec<OutboxRecord>> {
        let dir = self.outbox_dir();
        if !dir.exists() {
            return Ok(Vec::new());
        }
        let mut list = Vec::new();
        for entry in std::fs::read_dir(dir)? {
            let entry = entry?;
            let path = entry.path();
            if path.extension().and_then(|s| s.to_str()) == Some("json") {
                if let Ok(bytes) = std::fs::read(&path) {
                    if let Ok(rec) = serde_json::from_slice::<OutboxRecord>(&bytes) {
                        if rec.peer_id == peer_id {
                            list.push(rec);
                        }
                    }
                }
            }
        }
        list.sort_by_key(|r| r.created_at);
        Ok(list)
    }

    pub fn list_pending_messages_for_peer(
        &self,
        _local_peer_id: &str,
        _remote_peer_id: &str,
    ) -> Result<Vec<ChatMessage>> {
        Ok(Vec::new())
    }

    pub fn update_message_delivery(&self, _message_id: &str, _status: &str) -> Result<()> {
        Ok(())
    }

    pub fn record_shell_session_start(
        &self,
        ticket_id: &str,
        session_id: &str,
        operator_peer_id: &str,
        transport: &str,
    ) -> Result<()> {
        self.start_shell_session(ticket_id, session_id, operator_peer_id, transport)
            .map(|_| ())
    }

    pub fn record_shell_session_end(&self, session_id: &str, result: Option<&str>) -> Result<()> {
        let ticket_id = {
            let state = self.state.read().expect("state lock poisoned");
            state
                .tickets()
                .values()
                .find(|agg| agg.shell_sessions.iter().any(|s| s.id == session_id))
                .map(|agg| agg.record.id.clone())
        };

        if let Some(ticket_id) = ticket_id {
            self.end_shell_session(&ticket_id, session_id, result.map(|s| s.to_string()))?;
        }
        Ok(())
    }

    pub fn start_shell_session(
        &self,
        ticket_id: &str,
        session_id: &str,
        operator_peer_id: &str,
        transport: &str,
    ) -> Result<ShellSessionRecord> {
        let now = current_timestamp();

        let event = TicketEvent::ShellSessionStarted {
            ticket_id: ticket_id.to_string(),
            session_id: session_id.to_string(),
            operator_peer_id: operator_peer_id.to_string(),
            transport: transport.to_string(),
            started_at: now,
        };

        self.append_ticket_event(event, now)?;

        Ok(ShellSessionRecord {
            id: session_id.to_string(),
            ticket_id: ticket_id.to_string(),
            operator_peer_id: operator_peer_id.to_string(),
            started_at: now,
            ended_at: None,
            transport: transport.to_string(),
            result: None,
        })
    }

    pub fn end_shell_session(
        &self,
        ticket_id: &str,
        session_id: &str,
        result: Option<String>,
    ) -> Result<()> {
        let now = current_timestamp();
        let event = TicketEvent::ShellSessionEnded {
            ticket_id: ticket_id.to_string(),
            session_id: session_id.to_string(),
            ended_at: now,
            result,
        };

        self.append_ticket_event(event, now)?;
        Ok(())
    }

    /// Ingests or imports a ticket record received from a peer.
    pub fn import_ticket(&self, ticket: &TicketRecord) -> Result<()> {
        self.import_canonical_ticket(ticket)
    }

    pub fn import_canonical_ticket(&self, ticket: &TicketRecord) -> Result<()> {
        let existing = self.get_ticket(&ticket.id)?;
        match existing {
            None => {
                let event = TicketEvent::Created {
                    ticket_id: ticket.id.clone(),
                    title: ticket.title.clone(),
                    description: ticket.description.clone(),
                    priority: ticket.priority,
                    client_peer_id: ticket.client_peer_id.clone(),
                };
                self.append_ticket_event(event, ticket.created_at)?;
                if ticket.state != TicketState::Open {
                    let event2 = TicketEvent::StatusChanged {
                        ticket_id: ticket.id.clone(),
                        new_state: ticket.state,
                        actor_peer_id: ticket.client_peer_id.clone(),
                        reason: None,
                    };
                    self.append_ticket_event(event2, ticket.updated_at)?;
                }
            }
            Some(curr) => {
                if ticket.revision > curr.revision && ticket.state != curr.state {
                    let event = TicketEvent::StatusChanged {
                        ticket_id: ticket.id.clone(),
                        new_state: ticket.state,
                        actor_peer_id: ticket.client_peer_id.clone(),
                        reason: None,
                    };
                    self.append_ticket_event(event, ticket.updated_at)?;
                }
            }
        }
        Ok(())
    }

    /// Ingests a remote signed object received over the network (P2P synchronization).
    pub fn apply_remote_object(&self, obj: &SignedObject) -> Result<()> {
        obj.verify()
            .context("failed to verify remote SignedObject")?;
        let event = Event::from_signed_object(obj)?;

        // Persist object to disk
        self.store.put(obj)?;

        // Insert into graph
        {
            let mut graph = self.graph.write().expect("graph lock poisoned");
            graph.insert(event)?;
        }

        // Full deterministic reduction ensures out-of-order deliveries and DAG branches converge
        let graph = self.graph.read().expect("graph lock poisoned");
        let new_state = TicketReducer::reduce(&graph);
        *self.state.write().expect("state lock poisoned") = new_state;

        Ok(())
    }

    /// Ingests a batch of remote signed objects received over the network.
    pub fn apply_remote_objects(&self, objects: &[SignedObject]) -> Result<usize> {
        let mut count = 0;
        {
            let mut graph = self.graph.write().expect("graph lock poisoned");
            for obj in objects {
                if let Err(e) = obj.verify() {
                    tracing::warn!("Rejecting unverified remote object {}: {}", obj.id, e);
                    continue;
                }
                match Event::from_signed_object(obj) {
                    Ok(event) => {
                        if let Err(e) = self.store.put(obj) {
                            tracing::warn!("Failed to store remote object {}: {}", obj.id, e);
                            continue;
                        }
                        if let Err(e) = graph.insert(event) {
                            tracing::warn!("Failed to insert event {} into graph: {}", obj.id, e);
                            continue;
                        }
                        count += 1;
                    }
                    Err(e) => {
                        tracing::warn!("Failed to decode event from object {}: {}", obj.id, e);
                    }
                }
            }
        }

        if count > 0 {
            let graph = self.graph.read().expect("graph lock poisoned");
            let new_state = TicketReducer::reduce(&graph);
            *self.state.write().expect("state lock poisoned") = new_state;
        }

        Ok(count)
    }

    pub fn get_object(&self, id: &ObjectId) -> Result<Option<SignedObject>> {
        self.store.get(id)
    }

    pub fn get_objects(&self, ids: &[ObjectId]) -> Result<Vec<SignedObject>> {
        let mut objects = Vec::with_capacity(ids.len());
        for id in ids {
            if let Some(obj) = self.store.get(id)? {
                objects.push(obj);
            }
        }
        Ok(objects)
    }

    pub fn get_ticket_objects(&self, ticket_id: &str) -> Result<Vec<SignedObject>> {
        let ids = {
            let graph = self.graph.read().expect("graph lock poisoned");
            graph.ticket_event_ids(ticket_id)
        };
        self.get_objects(&ids)
    }

    pub fn ticket_heads(&self, ticket_id: &str) -> Vec<ObjectId> {
        let graph = self.graph.read().expect("graph lock poisoned");
        graph.ticket_heads(ticket_id)
    }

    pub fn missing_parents(&self) -> Vec<ObjectId> {
        let graph = self.graph.read().expect("graph lock poisoned");
        graph.missing_parents()
    }

    // ==========================================
    // Queries
    // ==========================================

    pub fn list_tickets(&self, state_filter: Option<TicketState>) -> Result<Vec<TicketRecord>> {
        let state = self.state.read().expect("state lock poisoned");
        Ok(state.list_tickets(state_filter))
    }

    pub fn get_ticket(&self, ticket_id: &str) -> Result<Option<TicketRecord>> {
        let state = self.state.read().expect("state lock poisoned");
        Ok(state.get_ticket(ticket_id))
    }

    pub fn get_ticket_detail(&self, ticket_id: &str) -> Result<Option<TicketDetail>> {
        let state = self.state.read().expect("state lock poisoned");
        Ok(state.get_ticket_detail(ticket_id))
    }

    pub fn list_messages(&self, ticket_id: &str) -> Result<Vec<ChatMessage>> {
        let state = self.state.read().expect("state lock poisoned");
        Ok(state.list_messages(ticket_id))
    }

    pub fn list_attachments(&self, ticket_id: &str) -> Result<Vec<AttachmentRecord>> {
        let state = self.state.read().expect("state lock poisoned");
        Ok(state.list_attachments(ticket_id))
    }

    pub fn list_shell_sessions(&self, ticket_id: &str) -> Result<Vec<ShellSessionRecord>> {
        let state = self.state.read().expect("state lock poisoned");
        Ok(state.list_shell_sessions(ticket_id))
    }

    pub fn get_active_ticket(&self) -> Result<Option<TicketRecord>> {
        let state = self.state.read().expect("state lock poisoned");
        Ok(state.get_active_ticket())
    }

    /// Backward compatibility helper for getting the active ticket
    pub async fn get(&self) -> Result<Option<crate::Ticket>> {
        if let Some(active) = self.get_active_ticket()? {
            return Ok(Some(crate::Ticket {
                id: active.id,
                state: active.state,
            }));
        }
        let all = self.list_tickets(None)?;
        if let Some(first) = all.first() {
            return Ok(Some(crate::Ticket {
                id: first.id.clone(),
                state: first.state,
            }));
        }
        Ok(None)
    }
}

#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct OutboxRecord {
    pub id: String,
    pub peer_id: String,
    pub kind: String,
    pub payload: String,
    pub created_at: u64,
}
