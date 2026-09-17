use anyhow::{Context, Result};
use rusqlite::{params, Connection, OptionalExtension};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};

fn current_timestamp() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs()
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub enum TicketState {
    Open,
    InProgress,
    Resolved,
    Closed,
}

impl TicketState {
    pub fn as_str(&self) -> &'static str {
        match self {
            Self::Open => "OPEN",
            Self::InProgress => "IN_PROGRESS",
            Self::Resolved => "RESOLVED",
            Self::Closed => "CLOSED",
        }
    }

    pub fn parse_str(s: &str) -> Option<Self> {
        s.parse().ok()
    }

    pub fn permits_work(&self) -> bool {
        matches!(self, Self::Open | Self::InProgress)
    }

    pub fn can_transition_to(self, next: Self) -> bool {
        self == next
            || matches!(
                (self, next),
                (Self::Open, Self::InProgress | Self::Resolved | Self::Closed)
                    | (Self::InProgress, Self::Resolved | Self::Closed)
                    | (Self::Resolved, Self::InProgress | Self::Closed)
            )
    }
}

impl std::str::FromStr for TicketState {
    type Err = anyhow::Error;

    fn from_str(s: &str) -> std::result::Result<Self, Self::Err> {
        match s {
            "OPEN" => Ok(Self::Open),
            "IN_PROGRESS" => Ok(Self::InProgress),
            "RESOLVED" => Ok(Self::Resolved),
            "CLOSED" => Ok(Self::Closed),
            _ => anyhow::bail!("Statut de ticket inconnu: {s}"),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub enum TicketPriority {
    Normal,
    High,
    Urgent,
}

impl TicketPriority {
    pub fn as_str(&self) -> &'static str {
        match self {
            Self::Normal => "NORMAL",
            Self::High => "HIGH",
            Self::Urgent => "URGENT",
        }
    }

    pub fn parse_str(s: &str) -> Self {
        s.parse().unwrap_or(Self::Normal)
    }
}

impl std::str::FromStr for TicketPriority {
    type Err = std::convert::Infallible;

    fn from_str(s: &str) -> std::result::Result<Self, Self::Err> {
        match s {
            "HIGH" => Ok(Self::High),
            "URGENT" => Ok(Self::Urgent),
            _ => Ok(Self::Normal),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TicketRecord {
    pub id: String,
    pub title: String,
    pub description: String,
    pub state: TicketState,
    pub priority: TicketPriority,
    pub client_peer_id: String,
    pub operator_peer_id: String,
    pub remote_access_enabled: bool,
    #[serde(default)]
    pub revision: u64,
    pub created_at: u64,
    pub updated_at: u64,
    pub closed_at: Option<u64>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ChatMessage {
    pub id: String,
    pub ticket_id: String,
    pub sender_peer_id: String,
    pub body: String,
    pub created_at: u64,
    pub delivery_state: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AttachmentRecord {
    pub id: String,
    pub ticket_id: String,
    pub sender_peer_id: String,
    pub filename: String,
    pub size_bytes: u64,
    pub sha256: String,
    pub local_path: String,
    pub created_at: u64,
    pub state: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ShellSessionRecord {
    pub id: String,
    pub ticket_id: String,
    pub operator_peer_id: String,
    pub started_at: u64,
    pub ended_at: Option<u64>,
    pub transport: String,
    pub result: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TicketEventRecord {
    pub id: String,
    pub ticket_id: String,
    pub kind: String,
    pub actor_peer_id: String,
    pub timestamp: u64,
    pub metadata: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TicketDetail {
    pub ticket: TicketRecord,
    pub messages: Vec<ChatMessage>,
    pub attachments: Vec<AttachmentRecord>,
    pub shell_sessions: Vec<ShellSessionRecord>,
    pub events: Vec<TicketEventRecord>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct OutboxRecord {
    pub id: String,
    pub peer_id: String,
    pub kind: String,
    pub payload: String,
    pub created_at: u64,
}

#[derive(Clone)]
pub struct TicketDb {
    conn: Arc<Mutex<Connection>>,
    db_path: Option<PathBuf>,
}

impl TicketDb {
    pub fn open(path: impl AsRef<Path>) -> Result<Self> {
        let path = path.as_ref();
        if let Some(parent) = path.parent().filter(|p| !p.as_os_str().is_empty()) {
            std::fs::create_dir_all(parent)
                .with_context(|| format!("failed to create db directory {}", parent.display()))?;
        }
        let conn = Connection::open(path)
            .with_context(|| format!("failed to open sqlite database at {}", path.display()))?;
        let db = Self {
            conn: Arc::new(Mutex::new(conn)),
            db_path: Some(path.to_path_buf()),
        };
        db.init_schema()?;
        Ok(db)
    }

    pub fn open_in_memory() -> Result<Self> {
        let conn =
            Connection::open_in_memory().context("failed to open in-memory sqlite database")?;
        let db = Self {
            conn: Arc::new(Mutex::new(conn)),
            db_path: None,
        };
        db.init_schema()?;
        Ok(db)
    }

    pub fn path(&self) -> Option<&Path> {
        self.db_path.as_deref()
    }

    fn init_schema(&self) -> Result<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute_batch(
            "
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS tickets (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                description TEXT NOT NULL,
                state TEXT NOT NULL,
                priority TEXT NOT NULL,
                client_peer_id TEXT NOT NULL,
                operator_peer_id TEXT NOT NULL,
                remote_access_enabled INTEGER NOT NULL DEFAULT 1,
                revision INTEGER NOT NULL DEFAULT 1,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                closed_at INTEGER
            );

            CREATE TABLE IF NOT EXISTS messages (
                id TEXT PRIMARY KEY,
                ticket_id TEXT NOT NULL REFERENCES tickets(id) ON DELETE CASCADE,
                sender_peer_id TEXT NOT NULL,
                body TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                delivery_state TEXT NOT NULL DEFAULT 'DELIVERED'
            );

            CREATE TABLE IF NOT EXISTS attachments (
                id TEXT PRIMARY KEY,
                ticket_id TEXT NOT NULL REFERENCES tickets(id) ON DELETE CASCADE,
                sender_peer_id TEXT NOT NULL,
                filename TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                sha256 TEXT NOT NULL,
                local_path TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                state TEXT NOT NULL DEFAULT 'COMPLETED'
            );

            CREATE TABLE IF NOT EXISTS shell_sessions (
                id TEXT PRIMARY KEY,
                ticket_id TEXT NOT NULL REFERENCES tickets(id) ON DELETE CASCADE,
                operator_peer_id TEXT NOT NULL,
                started_at INTEGER NOT NULL,
                ended_at INTEGER,
                transport TEXT NOT NULL,
                result TEXT
            );

            CREATE TABLE IF NOT EXISTS ticket_events (
                id TEXT PRIMARY KEY,
                ticket_id TEXT NOT NULL REFERENCES tickets(id) ON DELETE CASCADE,
                kind TEXT NOT NULL,
                actor_peer_id TEXT NOT NULL,
                timestamp INTEGER NOT NULL,
                metadata TEXT
            );

            CREATE TABLE IF NOT EXISTS outbox (
                id TEXT PRIMARY KEY,
                peer_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                payload TEXT NOT NULL,
                created_at INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_tickets_state ON tickets(state);
            CREATE INDEX IF NOT EXISTS idx_messages_ticket ON messages(ticket_id, created_at);
            CREATE INDEX IF NOT EXISTS idx_attachments_ticket ON attachments(ticket_id);
            CREATE INDEX IF NOT EXISTS idx_shell_sessions_ticket ON shell_sessions(ticket_id);
            CREATE INDEX IF NOT EXISTS idx_events_ticket ON ticket_events(ticket_id, timestamp);
            CREATE INDEX IF NOT EXISTS idx_outbox_peer ON outbox(peer_id, created_at);
            ",
        )
        .context("failed to execute sqlite schema migration")?;
        let has_revision = {
            let mut stmt = conn.prepare("PRAGMA table_info(tickets)")?;
            let columns = stmt.query_map([], |row| row.get::<_, String>(1))?;
            let mut found = false;
            for column in columns {
                if column? == "revision" {
                    found = true;
                    break;
                }
            }
            found
        };
        if !has_revision {
            conn.execute(
                "ALTER TABLE tickets ADD COLUMN revision INTEGER NOT NULL DEFAULT 1",
                [],
            )?;
        }
        Ok(())
    }

    pub fn create_ticket(
        &self,
        title: &str,
        description: &str,
        priority: TicketPriority,
        client_peer_id: &str,
        operator_peer_id: &str,
    ) -> Result<TicketRecord> {
        let now = current_timestamp();
        let id = format!("FTQ-{}", uuid::Uuid::new_v4().simple());
        let record = TicketRecord {
            id: id.clone(),
            title: title.to_string(),
            description: description.to_string(),
            state: TicketState::Open,
            priority,
            client_peer_id: client_peer_id.to_string(),
            operator_peer_id: operator_peer_id.to_string(),
            remote_access_enabled: true,
            revision: 1,
            created_at: now,
            updated_at: now,
            closed_at: None,
        };

        {
            let conn = self.conn.lock().unwrap();
            conn.execute(
                "INSERT INTO tickets (
                    id, title, description, state, priority,
                    client_peer_id, operator_peer_id, remote_access_enabled, revision,
                    created_at, updated_at, closed_at
                ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12)",
                params![
                    record.id,
                    record.title,
                    record.description,
                    record.state.as_str(),
                    record.priority.as_str(),
                    record.client_peer_id,
                    record.operator_peer_id,
                    if record.remote_access_enabled { 1 } else { 0 },
                    record.revision,
                    record.created_at,
                    record.updated_at,
                    record.closed_at,
                ],
            )
            .context("failed to insert ticket record")?;
        }

        self.record_event(
            &id,
            "CREATED",
            client_peer_id,
            Some(&format!("Priorité: {}", priority.as_str())),
        )?;

        Ok(record)
    }

    pub fn import_ticket(&self, ticket: &TicketRecord) -> Result<()> {
        let conn = self.conn.lock().unwrap();
        let local_revision: Option<u64> = conn
            .query_row(
                "SELECT revision FROM tickets WHERE id = ?1",
                params![ticket.id],
                |row| row.get(0),
            )
            .optional()?;
        if let Some(local_revision) = local_revision {
            if ticket.revision < local_revision {
                anyhow::bail!(
                    "stale ticket revision {} (local revision is {})",
                    ticket.revision,
                    local_revision
                );
            }
            if ticket.revision == local_revision {
                return Ok(());
            }
        } else if ticket.revision == 0 {
            anyhow::bail!("ticket revision must be greater than zero");
        }
        conn.execute(
            "INSERT INTO tickets (
                id, title, description, state, priority,
                client_peer_id, operator_peer_id, remote_access_enabled, revision,
                created_at, updated_at, closed_at
            ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12)
            ON CONFLICT(id) DO UPDATE SET
                title = excluded.title,
                description = excluded.description,
                state = excluded.state,
                priority = excluded.priority,
                remote_access_enabled = excluded.remote_access_enabled,
                revision = excluded.revision,
                updated_at = excluded.updated_at,
                closed_at = excluded.closed_at",
            params![
                ticket.id,
                ticket.title,
                ticket.description,
                ticket.state.as_str(),
                ticket.priority.as_str(),
                ticket.client_peer_id,
                ticket.operator_peer_id,
                if ticket.remote_access_enabled { 1 } else { 0 },
                ticket.revision,
                ticket.created_at,
                ticket.updated_at,
                ticket.closed_at,
            ],
        )
        .context("failed to import ticket record")?;
        Ok(())
    }

    pub fn get_ticket(&self, ticket_id: &str) -> Result<Option<TicketRecord>> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "SELECT id, title, description, state, priority, client_peer_id, operator_peer_id,
                    remote_access_enabled, revision, created_at, updated_at, closed_at
             FROM tickets WHERE id = ?1",
        )?;
        let row = stmt
            .query_row(params![ticket_id], |r| {
                let state_str: String = r.get(3)?;
                let priority_str: String = r.get(4)?;
                let remote_access_int: i64 = r.get(7)?;
                Ok(TicketRecord {
                    id: r.get(0)?,
                    title: r.get(1)?,
                    description: r.get(2)?,
                    state: TicketState::parse_str(&state_str).unwrap_or(TicketState::Open),
                    priority: TicketPriority::parse_str(&priority_str),
                    client_peer_id: r.get(5)?,
                    operator_peer_id: r.get(6)?,
                    remote_access_enabled: remote_access_int != 0,
                    revision: r.get(8)?,
                    created_at: r.get(9)?,
                    updated_at: r.get(10)?,
                    closed_at: r.get(11)?,
                })
            })
            .optional()?;
        Ok(row)
    }

    pub fn get_active_ticket(&self) -> Result<Option<TicketRecord>> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "SELECT id, title, description, state, priority, client_peer_id, operator_peer_id,
                    remote_access_enabled, revision, created_at, updated_at, closed_at
             FROM tickets WHERE state IN ('OPEN', 'IN_PROGRESS')
             ORDER BY updated_at DESC LIMIT 1",
        )?;
        let row = stmt
            .query_row([], |r| {
                let state_str: String = r.get(3)?;
                let priority_str: String = r.get(4)?;
                let remote_access_int: i64 = r.get(7)?;
                Ok(TicketRecord {
                    id: r.get(0)?,
                    title: r.get(1)?,
                    description: r.get(2)?,
                    state: TicketState::parse_str(&state_str).unwrap_or(TicketState::Open),
                    priority: TicketPriority::parse_str(&priority_str),
                    client_peer_id: r.get(5)?,
                    operator_peer_id: r.get(6)?,
                    remote_access_enabled: remote_access_int != 0,
                    revision: r.get(8)?,
                    created_at: r.get(9)?,
                    updated_at: r.get(10)?,
                    closed_at: r.get(11)?,
                })
            })
            .optional()?;
        Ok(row)
    }

    pub fn list_tickets(&self, state_filter: Option<TicketState>) -> Result<Vec<TicketRecord>> {
        let conn = self.conn.lock().unwrap();
        let mut query =
            "SELECT id, title, description, state, priority, client_peer_id, operator_peer_id,
                                remote_access_enabled, revision, created_at, updated_at, closed_at
                         FROM tickets"
                .to_string();
        if let Some(state) = state_filter {
            query.push_str(&format!(" WHERE state = '{}'", state.as_str()));
        }
        query.push_str(" ORDER BY created_at DESC");

        let mut stmt = conn.prepare(&query)?;
        let rows = stmt.query_map([], |r| {
            let state_str: String = r.get(3)?;
            let priority_str: String = r.get(4)?;
            let remote_access_int: i64 = r.get(7)?;
            Ok(TicketRecord {
                id: r.get(0)?,
                title: r.get(1)?,
                description: r.get(2)?,
                state: TicketState::parse_str(&state_str).unwrap_or(TicketState::Open),
                priority: TicketPriority::parse_str(&priority_str),
                client_peer_id: r.get(5)?,
                operator_peer_id: r.get(6)?,
                remote_access_enabled: remote_access_int != 0,
                revision: r.get(8)?,
                created_at: r.get(9)?,
                updated_at: r.get(10)?,
                closed_at: r.get(11)?,
            })
        })?;

        let mut list = Vec::new();
        for row in rows {
            list.push(row?);
        }
        Ok(list)
    }

    pub fn update_ticket_state(
        &self,
        ticket_id: &str,
        new_state: TicketState,
        actor_peer_id: &str,
    ) -> Result<Option<TicketRecord>> {
        let Some(current) = self.get_ticket(ticket_id)? else {
            return Ok(None);
        };
        if !current.state.can_transition_to(new_state) {
            anyhow::bail!(
                "invalid ticket state transition: {} -> {}",
                current.state.as_str(),
                new_state.as_str()
            );
        }
        if current.state == new_state {
            return Ok(Some(current));
        }
        let now = current_timestamp();
        {
            let conn = self.conn.lock().unwrap();
            let affected = conn.execute(
                "UPDATE tickets SET state = ?1, updated_at = ?2, revision = revision + 1,
                        closed_at = CASE WHEN ?1 = 'CLOSED' THEN ?2 ELSE closed_at END
                 WHERE id = ?3",
                params![new_state.as_str(), now, ticket_id],
            )?;
            if affected == 0 {
                return Ok(None);
            }
        }

        self.record_event(
            ticket_id,
            "STATUS_CHANGED",
            actor_peer_id,
            Some(new_state.as_str()),
        )?;

        self.get_ticket(ticket_id)
    }

    pub fn set_remote_access(
        &self,
        ticket_id: &str,
        enabled: bool,
        actor_peer_id: &str,
    ) -> Result<Option<TicketRecord>> {
        let Some(current) = self.get_ticket(ticket_id)? else {
            return Ok(None);
        };
        if current.remote_access_enabled == enabled {
            return Ok(Some(current));
        }
        let now = current_timestamp();
        {
            let conn = self.conn.lock().unwrap();
            let affected = conn.execute(
                "UPDATE tickets SET remote_access_enabled = ?1, updated_at = ?2,
                        revision = revision + 1 WHERE id = ?3",
                params![if enabled { 1 } else { 0 }, now, ticket_id],
            )?;
            if affected == 0 {
                return Ok(None);
            }
        }

        self.record_event(
            ticket_id,
            "REMOTE_ACCESS_TOGGLED",
            actor_peer_id,
            Some(if enabled { "ENABLED" } else { "DISABLED" }),
        )?;

        self.get_ticket(ticket_id)
    }

    pub fn add_chat_message(&self, message: &ChatMessage) -> Result<bool> {
        let conn = self.conn.lock().unwrap();
        let affected = conn.execute(
            "INSERT OR IGNORE INTO messages (id, ticket_id, sender_peer_id, body, created_at, delivery_state)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
            params![
                message.id,
                message.ticket_id,
                message.sender_peer_id,
                message.body,
                message.created_at,
                message.delivery_state,
            ],
        )?;
        Ok(affected > 0)
    }

    pub fn list_messages(&self, ticket_id: &str) -> Result<Vec<ChatMessage>> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "SELECT id, ticket_id, sender_peer_id, body, created_at, delivery_state
             FROM messages WHERE ticket_id = ?1 ORDER BY created_at ASC",
        )?;
        let rows = stmt.query_map(params![ticket_id], |r| {
            Ok(ChatMessage {
                id: r.get(0)?,
                ticket_id: r.get(1)?,
                sender_peer_id: r.get(2)?,
                body: r.get(3)?,
                created_at: r.get(4)?,
                delivery_state: r.get(5)?,
            })
        })?;
        let mut list = Vec::new();
        for row in rows {
            list.push(row?);
        }
        Ok(list)
    }

    pub fn update_message_delivery(&self, id: &str, delivery_state: &str) -> Result<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute(
            "UPDATE messages SET delivery_state = ?1 WHERE id = ?2",
            params![delivery_state, id],
        )?;
        Ok(())
    }

    pub fn list_pending_messages_for_peer(
        &self,
        local_peer_id: &str,
        remote_peer_id: &str,
    ) -> Result<Vec<ChatMessage>> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "SELECT m.id, m.ticket_id, m.sender_peer_id, m.body, m.created_at, m.delivery_state
             FROM messages m
             JOIN tickets t ON t.id = m.ticket_id
             WHERE m.delivery_state = 'PENDING'
               AND m.sender_peer_id = ?1
               AND t.state IN ('OPEN', 'IN_PROGRESS')
               AND ((t.client_peer_id = ?1 AND t.operator_peer_id = ?2)
                 OR (t.operator_peer_id = ?1 AND t.client_peer_id = ?2))
             ORDER BY m.created_at ASC",
        )?;
        let rows = stmt.query_map(params![local_peer_id, remote_peer_id], |r| {
            Ok(ChatMessage {
                id: r.get(0)?,
                ticket_id: r.get(1)?,
                sender_peer_id: r.get(2)?,
                body: r.get(3)?,
                created_at: r.get(4)?,
                delivery_state: r.get(5)?,
            })
        })?;
        let mut list = Vec::new();
        for row in rows {
            list.push(row?);
        }
        Ok(list)
    }

    pub fn enqueue_outbox(&self, peer_id: &str, kind: &str, payload: &str) -> Result<String> {
        let id = uuid::Uuid::new_v4().to_string();
        let conn = self.conn.lock().unwrap();
        conn.execute(
            "INSERT INTO outbox (id, peer_id, kind, payload, created_at)
             VALUES (?1, ?2, ?3, ?4, ?5)",
            params![id, peer_id, kind, payload, current_timestamp()],
        )?;
        Ok(id)
    }

    pub fn list_outbox_for_peer(&self, peer_id: &str) -> Result<Vec<OutboxRecord>> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "SELECT id, peer_id, kind, payload, created_at
             FROM outbox WHERE peer_id = ?1 ORDER BY created_at ASC",
        )?;
        let rows = stmt.query_map(params![peer_id], |r| {
            Ok(OutboxRecord {
                id: r.get(0)?,
                peer_id: r.get(1)?,
                kind: r.get(2)?,
                payload: r.get(3)?,
                created_at: r.get(4)?,
            })
        })?;
        let mut list = Vec::new();
        for row in rows {
            list.push(row?);
        }
        Ok(list)
    }

    pub fn remove_outbox(&self, id: &str) -> Result<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute("DELETE FROM outbox WHERE id = ?1", params![id])?;
        Ok(())
    }

    pub fn add_attachment(&self, attachment: &AttachmentRecord) -> Result<()> {
        let conn = self.conn.lock().unwrap();
        conn.execute(
            "INSERT INTO attachments (id, ticket_id, sender_peer_id, filename, size_bytes, sha256, local_path, created_at, state)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)
             ON CONFLICT(id) DO UPDATE SET
                filename = excluded.filename,
                size_bytes = excluded.size_bytes,
                sha256 = excluded.sha256,
                local_path = excluded.local_path,
                state = excluded.state",
            params![
                attachment.id,
                attachment.ticket_id,
                attachment.sender_peer_id,
                attachment.filename,
                attachment.size_bytes,
                attachment.sha256,
                attachment.local_path,
                attachment.created_at,
                attachment.state,
            ],
        )?;
        Ok(())
    }

    pub fn list_attachments(&self, ticket_id: &str) -> Result<Vec<AttachmentRecord>> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "SELECT id, ticket_id, sender_peer_id, filename, size_bytes, sha256, local_path, created_at, state
             FROM attachments WHERE ticket_id = ?1 ORDER BY created_at ASC",
        )?;
        let rows = stmt.query_map(params![ticket_id], |r| {
            Ok(AttachmentRecord {
                id: r.get(0)?,
                ticket_id: r.get(1)?,
                sender_peer_id: r.get(2)?,
                filename: r.get(3)?,
                size_bytes: r.get(4)?,
                sha256: r.get(5)?,
                local_path: r.get(6)?,
                created_at: r.get(7)?,
                state: r.get(8)?,
            })
        })?;
        let mut list = Vec::new();
        for row in rows {
            list.push(row?);
        }
        Ok(list)
    }

    pub fn record_shell_session_start(
        &self,
        id: &str,
        ticket_id: &str,
        operator_peer_id: &str,
        transport: &str,
    ) -> Result<()> {
        let now = current_timestamp();
        let conn = self.conn.lock().unwrap();
        conn.execute(
            "INSERT INTO shell_sessions (id, ticket_id, operator_peer_id, started_at, transport)
             VALUES (?1, ?2, ?3, ?4, ?5)",
            params![id, ticket_id, operator_peer_id, now, transport],
        )?;
        Ok(())
    }

    pub fn record_shell_session_end(&self, id: &str, result: Option<&str>) -> Result<()> {
        let now = current_timestamp();
        let conn = self.conn.lock().unwrap();
        conn.execute(
            "UPDATE shell_sessions SET ended_at = ?1, result = ?2 WHERE id = ?3",
            params![now, result, id],
        )?;
        Ok(())
    }

    pub fn list_shell_sessions(&self, ticket_id: &str) -> Result<Vec<ShellSessionRecord>> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "SELECT id, ticket_id, operator_peer_id, started_at, ended_at, transport, result
             FROM shell_sessions WHERE ticket_id = ?1 ORDER BY started_at DESC",
        )?;
        let rows = stmt.query_map(params![ticket_id], |r| {
            Ok(ShellSessionRecord {
                id: r.get(0)?,
                ticket_id: r.get(1)?,
                operator_peer_id: r.get(2)?,
                started_at: r.get(3)?,
                ended_at: r.get(4)?,
                transport: r.get(5)?,
                result: r.get(6)?,
            })
        })?;
        let mut list = Vec::new();
        for row in rows {
            list.push(row?);
        }
        Ok(list)
    }

    pub fn record_event(
        &self,
        ticket_id: &str,
        kind: &str,
        actor_peer_id: &str,
        metadata: Option<&str>,
    ) -> Result<()> {
        let now = current_timestamp();
        let id = uuid::Uuid::new_v4().to_string();
        let conn = self.conn.lock().unwrap();
        conn.execute(
            "INSERT INTO ticket_events (id, ticket_id, kind, actor_peer_id, timestamp, metadata)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
            params![id, ticket_id, kind, actor_peer_id, now, metadata],
        )?;
        Ok(())
    }

    pub fn list_events(&self, ticket_id: &str) -> Result<Vec<TicketEventRecord>> {
        let conn = self.conn.lock().unwrap();
        let mut stmt = conn.prepare(
            "SELECT id, ticket_id, kind, actor_peer_id, timestamp, metadata
             FROM ticket_events WHERE ticket_id = ?1 ORDER BY timestamp ASC",
        )?;
        let rows = stmt.query_map(params![ticket_id], |r| {
            Ok(TicketEventRecord {
                id: r.get(0)?,
                ticket_id: r.get(1)?,
                kind: r.get(2)?,
                actor_peer_id: r.get(3)?,
                timestamp: r.get(4)?,
                metadata: r.get(5)?,
            })
        })?;
        let mut list = Vec::new();
        for row in rows {
            list.push(row?);
        }
        Ok(list)
    }

    pub fn get_ticket_detail(&self, ticket_id: &str) -> Result<Option<TicketDetail>> {
        let Some(ticket) = self.get_ticket(ticket_id)? else {
            return Ok(None);
        };
        let messages = self.list_messages(ticket_id)?;
        let attachments = self.list_attachments(ticket_id)?;
        let shell_sessions = self.list_shell_sessions(ticket_id)?;
        let events = self.list_events(ticket_id)?;
        Ok(Some(TicketDetail {
            ticket,
            messages,
            attachments,
            shell_sessions,
            events,
        }))
    }

    pub fn migrate_from_legacy_file(
        &self,
        legacy_file: &Path,
        client_peer_id: &str,
        operator_peer_id: &str,
    ) -> Result<()> {
        if !legacy_file.exists() {
            return Ok(());
        }
        let conn = self.conn.lock().unwrap();
        let count: i64 = conn.query_row("SELECT COUNT(*) FROM tickets", [], |r| r.get(0))?;
        if count > 0 {
            return Ok(());
        }
        drop(conn);

        let data = std::fs::read(legacy_file)?;
        #[derive(Deserialize)]
        struct LegacyTicket {
            id: String,
            state: String,
        }
        if let Ok(legacy) = serde_json::from_slice::<LegacyTicket>(&data) {
            let state = if legacy.state.eq_ignore_ascii_case("OPEN") {
                TicketState::Open
            } else {
                TicketState::Closed
            };
            let now = current_timestamp();
            let record = TicketRecord {
                id: legacy.id,
                title: "Assistance initiale".to_string(),
                description: "Ticket migré depuis le format legacy ticket.json".to_string(),
                state,
                priority: TicketPriority::Normal,
                client_peer_id: client_peer_id.to_string(),
                operator_peer_id: operator_peer_id.to_string(),
                remote_access_enabled: state.permits_work(),
                revision: 1,
                created_at: now,
                updated_at: now,
                closed_at: if state == TicketState::Closed {
                    Some(now)
                } else {
                    None
                },
            };
            self.import_ticket(&record)?;
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ticket_lifecycle_and_events() {
        let db = TicketDb::open_in_memory().unwrap();
        let ticket = db
            .create_ticket(
                "VPN issue",
                "Cannot connect to office network",
                TicketPriority::High,
                "client_1",
                "operator_1",
            )
            .unwrap();

        assert_eq!(ticket.title, "VPN issue");
        assert_eq!(ticket.state, TicketState::Open);
        assert_eq!(ticket.priority, TicketPriority::High);
        assert!(ticket.remote_access_enabled);

        let retrieved = db.get_ticket(&ticket.id).unwrap().unwrap();
        assert_eq!(retrieved.id, ticket.id);

        let updated = db
            .update_ticket_state(&ticket.id, TicketState::InProgress, "operator_1")
            .unwrap()
            .unwrap();
        assert_eq!(updated.state, TicketState::InProgress);

        // Toggle remote access off
        let toggled = db
            .set_remote_access(&ticket.id, false, "client_1")
            .unwrap()
            .unwrap();
        assert!(!toggled.remote_access_enabled);

        // Events check
        let events = db.list_events(&ticket.id).unwrap();
        assert_eq!(events.len(), 3); // CREATED, STATUS_CHANGED, REMOTE_ACCESS_TOGGLED
    }

    #[test]
    fn chat_idempotency_and_ordering() {
        let db = TicketDb::open_in_memory().unwrap();
        let ticket = db
            .create_ticket(
                "Chat test",
                "Testing chat",
                TicketPriority::Normal,
                "c1",
                "op1",
            )
            .unwrap();

        let msg1 = ChatMessage {
            id: "msg_001".to_string(),
            ticket_id: ticket.id.clone(),
            sender_peer_id: "c1".to_string(),
            body: "Bonjour".to_string(),
            created_at: 1000,
            delivery_state: "DELIVERED".to_string(),
        };

        let inserted1 = db.add_chat_message(&msg1).unwrap();
        assert!(inserted1);

        // Retry same message (idempotency check)
        let inserted2 = db.add_chat_message(&msg1).unwrap();
        assert!(!inserted2, "Duplicate message should not be re-inserted");

        let msg2 = ChatMessage {
            id: "msg_002".to_string(),
            ticket_id: ticket.id.clone(),
            sender_peer_id: "op1".to_string(),
            body: "Je vous écoute".to_string(),
            created_at: 1005,
            delivery_state: "DELIVERED".to_string(),
        };
        db.add_chat_message(&msg2).unwrap();

        let messages = db.list_messages(&ticket.id).unwrap();
        assert_eq!(messages.len(), 2);
        assert_eq!(messages[0].body, "Bonjour");
        assert_eq!(messages[1].body, "Je vous écoute");
    }

    #[test]
    fn pending_chat_outbox_is_scoped_to_authenticated_counterparty() {
        let db = TicketDb::open_in_memory().unwrap();
        let ticket = db
            .create_ticket("Outbox", "offline", TicketPriority::Normal, "c1", "op1")
            .unwrap();
        db.add_chat_message(&ChatMessage {
            id: "pending-1".to_string(),
            ticket_id: ticket.id,
            sender_peer_id: "c1".to_string(),
            body: "queued".to_string(),
            created_at: 1,
            delivery_state: "PENDING".to_string(),
        })
        .unwrap();

        assert_eq!(
            db.list_pending_messages_for_peer("c1", "op1")
                .unwrap()
                .len(),
            1
        );
        assert!(db
            .list_pending_messages_for_peer("c1", "intruder")
            .unwrap()
            .is_empty());
        assert!(db
            .list_pending_messages_for_peer("op1", "c1")
            .unwrap()
            .is_empty());
    }

    #[test]
    fn ticket_sync_outbox_is_persistent_and_peer_scoped() {
        let temp_dir = tempfile::tempdir().unwrap();
        let db_path = temp_dir.path().join("outbox.db");
        let id = {
            let db = TicketDb::open(&db_path).unwrap();
            db.enqueue_outbox("peer-a", "TICKET_SYNC", r#"{"operation":"close"}"#)
                .unwrap()
        };

        let reopened = TicketDb::open(&db_path).unwrap();
        let records = reopened.list_outbox_for_peer("peer-a").unwrap();
        assert_eq!(records.len(), 1);
        assert_eq!(records[0].id, id);
        assert!(reopened.list_outbox_for_peer("peer-b").unwrap().is_empty());

        reopened.remove_outbox(&id).unwrap();
        assert!(reopened.list_outbox_for_peer("peer-a").unwrap().is_empty());
    }

    #[test]
    fn attachments_and_shell_sessions() {
        let db = TicketDb::open_in_memory().unwrap();
        let ticket = db
            .create_ticket("File/Shell", "desc", TicketPriority::Normal, "c1", "op1")
            .unwrap();

        let att = AttachmentRecord {
            id: "att_1".to_string(),
            ticket_id: ticket.id.clone(),
            sender_peer_id: "c1".to_string(),
            filename: "logs.zip".to_string(),
            size_bytes: 1024,
            sha256: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855".to_string(),
            local_path: "/tmp/logs.zip".to_string(),
            created_at: 2000,
            state: "COMPLETED".to_string(),
        };
        db.add_attachment(&att).unwrap();
        let atts = db.list_attachments(&ticket.id).unwrap();
        assert_eq!(atts.len(), 1);
        assert_eq!(atts[0].filename, "logs.zip");

        // Shell session
        db.record_shell_session_start("sess_1", &ticket.id, "op1", "RELAY CIRCUIT")
            .unwrap();
        db.record_shell_session_end("sess_1", Some("exit code 0"))
            .unwrap();

        let sessions = db.list_shell_sessions(&ticket.id).unwrap();
        assert_eq!(sessions.len(), 1);
        assert_eq!(sessions[0].transport, "RELAY CIRCUIT");
        assert_eq!(sessions[0].result.as_deref(), Some("exit code 0"));
    }

    #[test]
    fn legacy_migration() {
        let temp_dir = tempfile::tempdir().unwrap();
        let legacy_file = temp_dir.path().join("ticket.json");
        std::fs::write(
            &legacy_file,
            r#"{"id": "legacy-uuid-123", "state": "OPEN"}"#,
        )
        .unwrap();

        let db_file = temp_dir.path().join("tickets.db");
        let db = TicketDb::open(&db_file).unwrap();
        db.migrate_from_legacy_file(&legacy_file, "client_peer", "op_peer")
            .unwrap();

        let tickets = db.list_tickets(None).unwrap();
        assert_eq!(tickets.len(), 1);
        assert_eq!(tickets[0].id, "legacy-uuid-123");
        assert_eq!(tickets[0].state, TicketState::Open);
        assert!(tickets[0].remote_access_enabled);
    }

    #[test]
    fn stale_ticket_import_cannot_roll_back_consent() {
        let db = TicketDb::open_in_memory().unwrap();
        let ticket = db
            .create_ticket(
                "Consent",
                "revision test",
                TicketPriority::Normal,
                "c1",
                "op1",
            )
            .unwrap();
        let stale = ticket.clone();

        let current = db
            .set_remote_access(&ticket.id, false, "c1")
            .unwrap()
            .unwrap();
        assert!(current.revision > stale.revision);
        assert!(db.import_ticket(&stale).is_err());

        let stored = db.get_ticket(&ticket.id).unwrap().unwrap();
        assert!(!stored.remote_access_enabled);
        assert_eq!(stored.revision, current.revision);
    }

    #[test]
    fn closed_ticket_cannot_be_reopened() {
        let db = TicketDb::open_in_memory().unwrap();
        let ticket = db
            .create_ticket(
                "State",
                "transition test",
                TicketPriority::Normal,
                "c1",
                "op1",
            )
            .unwrap();
        let closed = db
            .update_ticket_state(&ticket.id, TicketState::Closed, "c1")
            .unwrap()
            .unwrap();
        assert_eq!(closed.revision, ticket.revision + 1);

        assert!(db
            .update_ticket_state(&ticket.id, TicketState::Open, "c1")
            .is_err());
        assert_eq!(
            db.get_ticket(&ticket.id).unwrap().unwrap().state,
            TicketState::Closed
        );
    }
}
