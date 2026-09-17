import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import "@xterm/xterm/css/xterm.css";

export interface DesktopStatus {
  product: string;
  version: string;
  agentState: string;
  mode: string;
  peerId: string;
  activeTicketId?: string | null;
  activeTicketState?: string | null;
  authorizedOperator?: string | null;
}

export interface DesktopPeer {
  peerId: string;
  hostname: string;
  os: string;
  transport: string;
  status: string;
  mode?: string | null;
  authorizedOperator?: string | null;
  relay: boolean;
  rendezvous: boolean;
}

export interface TicketRecord {
  id: string;
  title: string;
  description: string;
  state: "OPEN" | "IN_PROGRESS" | "RESOLVED" | "CLOSED";
  priority: "NORMAL" | "HIGH" | "URGENT";
  client_peer_id: string;
  operator_peer_id: string;
  remote_access_enabled: boolean;
  created_at: number;
  updated_at: number;
  closed_at?: number | null;
}

export interface ChatMessage {
  id: string;
  ticket_id: string;
  sender_peer_id: string;
  body: string;
  created_at: number;
  delivery_state: string;
  revised_at?: number | null;
  revision_count?: number | null;
}

export interface SelfSupportDiagnosticsDto {
  os: string;
  arch: string;
  hostname: string;
  is_loopback_active: boolean;
  relay_bypassed: boolean;
  active_shards: number;
  event_packs_stored: number;
  canonical_heads: number;
  timestamp_secs: number;
}

export interface SelfSupportTicketDto {
  ticket_id: string;
  title: string;
  description: string;
  created_at: number;
  access_epoch: string;
  is_closed: boolean;
}

export interface OperatorSessionDto {
  operator_entity: string;
  capabilities: string[];
  issued_at: number;
  expires_at: number;
}

export interface AttachmentRecord {
  id: string;
  ticket_id: string;
  sender_peer_id: string;
  filename: string;
  size_bytes: number;
  sha256: string;
  local_path: string;
  created_at: number;
  state: string;
}

export interface ShellSessionRecord {
  id: string;
  ticket_id: string;
  operator_peer_id: string;
  started_at: number;
  ended_at?: number | null;
  transport: string;
  result?: string | null;
}

export interface TicketEventRecord {
  id: string;
  ticket_id: string;
  kind: string;
  actor_peer_id: string;
  timestamp: number;
  metadata?: string | null;
}

export interface TicketDetail {
  ticket: TicketRecord;
  messages: ChatMessage[];
  attachments: AttachmentRecord[];
  shell_sessions: ShellSessionRecord[];
  events: TicketEventRecord[];
}

// Application State
let currentPeerId = "";
let activeOperatorTab: "tickets" | "self-support" | "peers" | "settings" = "tickets";
let ticketFilter: "ALL" | "OPEN" | "IN_PROGRESS" | "RESOLVED" | "CLOSED" = "ALL";

let ticketsCache: TicketRecord[] = [];
let selectedTicketId: string | null = null;
let currentTicketDetail: TicketDetail | null = null;

// Managed client active ticket cache
let managedActiveTicket: TicketRecord | null = null;

// Terminal State
let term: Terminal | null = null;
let fitAddon: FitAddon | null = null;
let isTerminalActive = false;
let terminalTicketId: string | null = null;
let terminalSwitchGeneration = 0;
let terminalSwitchQueue: Promise<void> = Promise.resolve();
let refreshTimer: number | null = null;

function escapeHtml(text: string): string {
  const div = document.createElement("div");
  div.textContent = text;
  return div.innerHTML;
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function formatTimestamp(epochSecs: number): string {
  if (!epochSecs) return "—";
  const date = new Date(epochSecs * 1000);
  return date.toLocaleString();
}

function isPeerConnected(status: string): boolean {
  const normalized = status
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .toLowerCase();
  return normalized === "connecte" || normalized === "connected";
}

function applyMode(mode: "operator" | "managed", isOnline: boolean) {
  const operatorView = document.getElementById("operator-view");
  const selfSupportView = document.getElementById("self-support-view");
  const peersView = document.getElementById("peers-view");
  const settingsView = document.getElementById("settings-view");
  const managedView = document.getElementById("managed-view");
  const operatorTopbar = document.getElementById("operator-global-topbar");
  const navMenu = document.querySelector(".nav-menu") as HTMLElement | null;
  const brandModeEl = document.getElementById("brand-mode");
  const userRoleEl = document.getElementById("user-role");

  if (mode === "operator") {
    if (operatorView) operatorView.style.display = activeOperatorTab === "tickets" ? "grid" : "none";
    if (selfSupportView) selfSupportView.style.display = activeOperatorTab === "self-support" ? "grid" : "none";
    if (peersView) peersView.style.display = activeOperatorTab === "peers" ? "grid" : "none";
    if (settingsView) settingsView.style.display = activeOperatorTab === "settings" ? "grid" : "none";
    if (managedView) managedView.style.display = "none";
    if (operatorTopbar) operatorTopbar.style.display = "flex";
    if (navMenu) navMenu.style.display = "flex";
    if (brandModeEl) brandModeEl.textContent = "CONSOLE OPÉRATEUR";
    if (userRoleEl) userRoleEl.textContent = isOnline ? "OPÉRATEUR" : "DÉCONNECTÉ";
  } else {
    if (operatorView) operatorView.style.display = "none";
    if (selfSupportView) selfSupportView.style.display = "none";
    if (peersView) peersView.style.display = "none";
    if (settingsView) settingsView.style.display = "none";
    if (managedView) managedView.style.display = "flex";
    if (operatorTopbar) operatorTopbar.style.display = "none";
    if (navMenu) navMenu.style.display = "none";
    if (brandModeEl) brandModeEl.textContent = "CLIENT MANAGÉ";
    if (userRoleEl) userRoleEl.textContent = isOnline ? "CLIENT MANAGÉ" : "DÉCONNECTÉ";
  }
}

function setOperatorTab(tab: "tickets" | "self-support" | "peers" | "settings") {
  activeOperatorTab = tab;
  const operatorView = document.getElementById("operator-view");
  const selfSupportView = document.getElementById("self-support-view");
  const peersView = document.getElementById("peers-view");
  const settingsView = document.getElementById("settings-view");
  if (operatorView) operatorView.style.display = tab === "tickets" ? "grid" : "none";
  if (selfSupportView) selfSupportView.style.display = tab === "self-support" ? "grid" : "none";
  if (peersView) peersView.style.display = tab === "peers" ? "grid" : "none";
  if (settingsView) settingsView.style.display = tab === "settings" ? "grid" : "none";
  document.querySelectorAll<HTMLElement>(".nav-item").forEach((item) => {
    const selected = item.dataset.tab === tab;
    item.classList.toggle("active", selected);
    item.setAttribute("aria-selected", String(selected));
  });
  if (tab === "self-support") {
    loadSelfSupportDiagnostics();
  }
}

function setTicketSubTab(tab: "overview" | "chat" | "files" | "events" | "terminal") {
  document.querySelectorAll<HTMLElement>(".ticket-tab-btn[data-ttab]").forEach((btn) => {
    btn.classList.toggle("active", btn.dataset.ttab === tab);
  });
  const panes = ["overview", "chat", "files", "events", "terminal"];
  panes.forEach((p) => {
    const paneEl = document.getElementById(`ticket-pane-${p}`);
    if (paneEl) paneEl.style.display = p === tab ? (p === "overview" || p === "events" ? "block" : "flex") : "none";
  });
  if (tab === "terminal") {
    window.setTimeout(() => fitAddon?.fit(), 0);
  }
}

function setClientSubTab(tab: "chat" | "files") {
  document.querySelectorAll<HTMLElement>(".ticket-tab-btn[data-mttab]").forEach((btn) => {
    btn.classList.toggle("active", btn.dataset.mttab === tab);
  });
  const chatPane = document.getElementById("client-pane-chat");
  const filesPane = document.getElementById("client-pane-files");
  if (chatPane) chatPane.style.display = tab === "chat" ? "flex" : "none";
  if (filesPane) filesPane.style.display = tab === "files" ? "flex" : "none";
}

function updateStatusBadge(isOnline: boolean, stateText: string) {
  const statusDot = document.getElementById("status-dot");
  const agentStateEl = document.getElementById("agent-state");
  const managedDot = document.getElementById("managed-beacon-dot");
  const managedBeaconText = document.getElementById("managed-beacon-text");
  const opOfflineAlert = document.getElementById("operator-offline-alert");
  const managedOfflineAlert = document.getElementById("managed-offline-alert");

  if (isOnline) {
    if (statusDot) statusDot.className = "status-dot online";
    if (agentStateEl) {
      agentStateEl.textContent = stateText;
      agentStateEl.className = "user-state";
    }
    if (managedDot) managedDot.className = "status-dot online";
    if (managedBeaconText) {
      managedBeaconText.textContent = "Service Agent Actif · Réseau P2P Sécurisé";
      managedBeaconText.style.color = "#7ee787";
    }
    if (opOfflineAlert) opOfflineAlert.style.display = "none";
    if (managedOfflineAlert) managedOfflineAlert.style.display = "none";
  } else {
    if (statusDot) statusDot.className = "status-dot offline";
    if (agentStateEl) {
      agentStateEl.textContent = "Service Hors-Ligne";
      agentStateEl.className = "user-state offline";
    }
    if (managedDot) managedDot.className = "status-dot offline";
    if (managedBeaconText) {
      managedBeaconText.textContent = "Service Démon Indisponible";
      managedBeaconText.style.color = "var(--accent-red)";
    }
    if (opOfflineAlert) opOfflineAlert.style.display = "flex";
    if (managedOfflineAlert) managedOfflineAlert.style.display = "flex";
  }
}

// ----------------------------------------------------------------------------
// Operator Console: Tickets Rendering & Interactions
// ----------------------------------------------------------------------------

function renderTicketList(tickets: TicketRecord[], isOnline: boolean) {
  const container = document.getElementById("operator-ticket-list");
  const countBadge = document.getElementById("tickets-count-badge");
  if (!container) return;

  const filtered = tickets.filter((t) => {
    if (ticketFilter === "ALL") return true;
    return t.state === ticketFilter;
  });

  if (countBadge) {
    countBadge.textContent = `${filtered.length} Ticket${filtered.length > 1 ? "s" : ""}`;
    countBadge.className = filtered.length > 0 ? "badge open" : "badge";
  }

  if (!isOnline || filtered.length === 0) {
    container.innerHTML = `
      <div class="empty-state">
        <i class="ph ph-ticket"></i>
        <div class="empty-state-title">${isOnline ? "Aucun ticket trouvé" : "Service déconnecté"}</div>
        <div class="empty-state-subtitle">${
          isOnline
            ? "Aucun ticket ne correspond au filtre sélectionné."
            : "Démarrez fortiq-service pour synchroniser les tickets P2P."
        }</div>
      </div>
    `;
    if (!filtered.some((t) => t.id === selectedTicketId)) {
      clearTicketDetails();
    }
    return;
  }

  container.innerHTML = "";

  if (!selectedTicketId || !filtered.some((t) => t.id === selectedTicketId)) {
    selectedTicketId = filtered[0].id;
  }

  for (const ticket of filtered) {
    const card = document.createElement("div");
    const isSelected = ticket.id === selectedTicketId;
    card.className = `ticket-card ${isSelected ? "active" : ""}`;
    card.dataset.ticketId = ticket.id;
    card.tabIndex = 0;
    card.setAttribute("role", "button");

    let stateClass = "open";
    if (ticket.state === "IN_PROGRESS") stateClass = "active";
    else if (ticket.state === "RESOLVED") stateClass = "resolved";
    else if (ticket.state === "CLOSED") stateClass = "closed";

    let prioClass = "prio-normal";
    if (ticket.priority === "HIGH") prioClass = "prio-high";
    else if (ticket.priority === "URGENT") prioClass = "prio-urgent";

    const accessBadge = ticket.remote_access_enabled
      ? `<span class="badge online" title="Accès distant autorisé">ACCÈS DISTANT OK</span>`
      : `<span class="badge" title="Accès distant révoqué">ACCÈS BLOQUÉ</span>`;

    card.innerHTML = `
      <div class="ticket-card-header">
        <span class="ticket-badge ${stateClass}">${escapeHtml(ticket.state)}</span>
        <span class="ticket-priority ${prioClass}">${escapeHtml(ticket.priority)}</span>
        <span class="ticket-id">${escapeHtml(ticket.id.substring(0, 8))}</span>
      </div>
      <div class="ticket-card-title">${escapeHtml(ticket.title)}</div>
      <div class="ticket-card-meta">
        <span>Client: ${escapeHtml(ticket.client_peer_id.substring(0, 10))}...</span>
        ${accessBadge}
      </div>
    `;

    const selectTicket = () => {
      selectedTicketId = ticket.id;
      document.querySelectorAll(".ticket-card").forEach((c) => c.classList.remove("active"));
      card.classList.add("active");
      loadSelectedTicketDetail(ticket.id);
    };

    card.addEventListener("click", selectTicket);
    card.addEventListener("keydown", (e) => {
      if (e.key === "Enter" || e.key === " ") {
        e.preventDefault();
        selectTicket();
      }
    });

    container.appendChild(card);
  }

  if (selectedTicketId) {
    loadSelectedTicketDetail(selectedTicketId);
  }
}

function clearTicketDetails() {
  currentTicketDetail = null;
  const detailTitle = document.getElementById("detail-ticket-title");
  const detailSubtitle = document.getElementById("detail-ticket-subtitle");
  const stateBadge = document.getElementById("detail-state-badge");
  const accessBadge = document.getElementById("detail-access-badge");
  const ticketIdEl = document.getElementById("detail-ticket-id");
  const titleEl = document.getElementById("detail-title");
  const descEl = document.getElementById("detail-desc");
  const prioEl = document.getElementById("detail-priority");
  const clientPeerEl = document.getElementById("detail-client-peer");
  const operatorPeerEl = document.getElementById("detail-operator-peer");
  const createdAtEl = document.getElementById("detail-created-at");

  const btnTake = document.getElementById("btn-ticket-take") as HTMLButtonElement | null;
  const btnResolve = document.getElementById("btn-ticket-resolve") as HTMLButtonElement | null;
  const btnClose = document.getElementById("btn-close-ticket") as HTMLButtonElement | null;
  const btnTermToggle = document.getElementById("btn-terminal-toggle") as HTMLButtonElement | null;

  if (detailTitle) detailTitle.textContent = "Détails du Ticket";
  if (detailSubtitle) detailSubtitle.textContent = "Aucun ticket sélectionné";
  if (stateBadge) {
    stateBadge.textContent = "Aucun";
    stateBadge.className = "badge";
  }
  if (accessBadge) {
    accessBadge.textContent = "Accès Inconnu";
    accessBadge.className = "badge";
  }
  if (ticketIdEl) ticketIdEl.textContent = "—";
  if (titleEl) titleEl.textContent = "—";
  if (descEl) descEl.textContent = "—";
  if (prioEl) prioEl.textContent = "—";
  if (clientPeerEl) clientPeerEl.textContent = "—";
  if (operatorPeerEl) operatorPeerEl.textContent = "—";
  if (createdAtEl) createdAtEl.textContent = "—";

  if (btnTake) btnTake.disabled = true;
  if (btnResolve) btnResolve.disabled = true;
  if (btnClose) btnClose.disabled = true;
  if (btnTermToggle) btnTermToggle.disabled = true;

  // Clear Chat, Files, Events
  const chatMessages = document.getElementById("operator-chat-messages");
  if (chatMessages) {
    chatMessages.innerHTML = `<div class="empty-state"><div class="empty-state-subtitle">Sélectionnez un ticket pour afficher la discussion.</div></div>`;
  }
  const chatInput = document.getElementById("operator-chat-input") as HTMLInputElement | null;
  const btnChatSend = document.getElementById("btn-operator-chat-send") as HTMLButtonElement | null;
  if (chatInput) chatInput.disabled = true;
  if (btnChatSend) btnChatSend.disabled = true;

  const attList = document.getElementById("operator-attachments-list");
  if (attList) {
    attList.innerHTML = `<div class="empty-state"><div class="empty-state-subtitle">Sélectionnez un ticket pour afficher les fichiers.</div></div>`;
  }
  const filePathInput = document.getElementById("operator-file-path") as HTMLInputElement | null;
  const btnFileSend = document.getElementById("btn-operator-file-send") as HTMLButtonElement | null;
  if (filePathInput) filePathInput.disabled = true;
  if (btnFileSend) btnFileSend.disabled = true;

  const eventsTimeline = document.getElementById("operator-events-timeline");
  if (eventsTimeline) {
    eventsTimeline.innerHTML = `<div class="empty-state"><div class="empty-state-subtitle">Sélectionnez un ticket pour afficher les événements.</div></div>`;
  }
}

async function loadSelectedTicketDetail(ticketId: string) {
  try {
    if (isTerminalActive && terminalTicketId && terminalTicketId !== ticketId) {
      await invoke("close_terminal_session");
      isTerminalActive = false;
      terminalTicketId = null;
      term?.clear();
    }
    const detail = await invoke<TicketDetail | null>("get_ticket", { ticketId });
    if (!detail) {
      clearTicketDetails();
      return;
    }
    currentTicketDetail = detail;
    const shellAllowed = detail.ticket.state === "OPEN" || detail.ticket.state === "IN_PROGRESS";
    if (isTerminalActive && terminalTicketId === ticketId && !shellAllowed) {
      await invoke("close_terminal_session");
    }
    renderTicketOverview(detail.ticket);
    renderOperatorChat(detail.messages);
    renderOperatorAttachments(detail.attachments);
    renderOperatorEvents(detail.events);
  } catch (err) {
    console.error("Failed to get ticket detail:", err);
  }
}

function renderTicketOverview(ticket: TicketRecord) {
  const detailTitle = document.getElementById("detail-ticket-title");
  const detailSubtitle = document.getElementById("detail-ticket-subtitle");
  const stateBadge = document.getElementById("detail-state-badge");
  const accessBadge = document.getElementById("detail-access-badge");
  const ticketIdEl = document.getElementById("detail-ticket-id");
  const titleEl = document.getElementById("detail-title");
  const descEl = document.getElementById("detail-desc");
  const prioEl = document.getElementById("detail-priority");
  const clientPeerEl = document.getElementById("detail-client-peer");
  const operatorPeerEl = document.getElementById("detail-operator-peer");
  const createdAtEl = document.getElementById("detail-created-at");

  const btnTake = document.getElementById("btn-ticket-take") as HTMLButtonElement | null;
  const btnResolve = document.getElementById("btn-ticket-resolve") as HTMLButtonElement | null;
  const btnClose = document.getElementById("btn-close-ticket") as HTMLButtonElement | null;
  const btnTermToggle = document.getElementById("btn-terminal-toggle") as HTMLButtonElement | null;
  const chatInput = document.getElementById("operator-chat-input") as HTMLInputElement | null;
  const btnChatSend = document.getElementById("btn-operator-chat-send") as HTMLButtonElement | null;
  const filePathInput = document.getElementById("operator-file-path") as HTMLInputElement | null;
  const btnFileSend = document.getElementById("btn-operator-file-send") as HTMLButtonElement | null;

  if (detailTitle) detailTitle.textContent = `Ticket ${ticket.id.substring(0, 8)}`;
  if (detailSubtitle) detailSubtitle.textContent = ticket.title;

  if (stateBadge) {
    stateBadge.textContent = ticket.state;
    let badgeCls = "badge open";
    if (ticket.state === "IN_PROGRESS") badgeCls = "badge active";
    else if (ticket.state === "RESOLVED") badgeCls = "badge resolved";
    else if (ticket.state === "CLOSED") badgeCls = "badge closed";
    stateBadge.className = badgeCls;
  }

  if (accessBadge) {
    if (ticket.remote_access_enabled) {
      accessBadge.textContent = "Télé-assistance AUTORISÉE";
      accessBadge.className = "badge online";
    } else {
      accessBadge.textContent = "Télé-assistance RÉVOQUÉE";
      accessBadge.className = "badge";
    }
  }

  if (ticketIdEl) ticketIdEl.textContent = ticket.id;
  if (titleEl) titleEl.textContent = ticket.title;
  if (descEl) descEl.textContent = ticket.description || "Aucune description fournie.";
  if (prioEl) prioEl.textContent = ticket.priority;
  if (clientPeerEl) clientPeerEl.textContent = ticket.client_peer_id;
  if (operatorPeerEl) operatorPeerEl.textContent = ticket.operator_peer_id || "Non assigné";
  if (createdAtEl) createdAtEl.textContent = formatTimestamp(ticket.created_at);

  const isClosed = ticket.state === "CLOSED";
  const isResolved = ticket.state === "RESOLVED";
  const shellAllowed = ticket.state === "OPEN" || ticket.state === "IN_PROGRESS";

  if (btnTake) {
    btnTake.disabled = isClosed || ticket.state === "IN_PROGRESS";
  }
  if (btnResolve) {
    btnResolve.disabled = isClosed || isResolved;
  }
  if (btnClose) {
    btnClose.disabled = isClosed;
  }

  if (btnTermToggle) {
    const activeForTicket = isTerminalActive && terminalTicketId === ticket.id;
    btnTermToggle.disabled = !activeForTicket && (!shellAllowed || !ticket.remote_access_enabled);
    btnTermToggle.textContent = activeForTicket ? "Fermer le shell" : "Démarrer le shell";
  }

  if (chatInput) chatInput.disabled = isClosed;
  if (btnChatSend) btnChatSend.disabled = isClosed;
  if (filePathInput) filePathInput.disabled = isClosed;
  if (btnFileSend) btnFileSend.disabled = isClosed;

  // Update terminal denied banner if remote access is off or closed
  const deniedBanner = document.getElementById("terminal-denied-banner");
  const deniedMsg = document.getElementById("terminal-denied-msg");
  if (!shellAllowed) {
    if (deniedBanner) deniedBanner.style.display = "flex";
    if (deniedMsg) deniedMsg.textContent = "Shell indisponible : le ticket doit être ouvert ou en cours.";
  } else if (!ticket.remote_access_enabled) {
    if (deniedBanner) deniedBanner.style.display = "flex";
    if (deniedMsg) deniedMsg.textContent = "Accès refusé : Accès à distance révoqué par le client (DENIED_REMOTE_ACCESS_DISABLED).";
  } else {
    if (deniedBanner) deniedBanner.style.display = "none";
  }
}

function renderOperatorChat(messages: ChatMessage[]) {
  const container = document.getElementById("operator-chat-messages");
  if (!container) return;

  if (messages.length === 0) {
    container.innerHTML = `
      <div class="empty-state">
        <i class="ph ph-chats"></i>
        <div class="empty-state-subtitle">Aucun message échangé pour ce ticket.</div>
      </div>
    `;
    return;
  }

  container.innerHTML = messages
    .map((msg) => {
      const isMe = msg.sender_peer_id === currentPeerId;
      const bubbleClass = isMe ? "chat-bubble outgoing" : "chat-bubble incoming";
      const senderLabel = isMe ? "Moi (Opérateur)" : `Client (${msg.sender_peer_id.substring(0, 8)})`;
      const revisedBadge = (msg.revised_at || (msg.revision_count && msg.revision_count > 0))
        ? `<span class="chat-revised" style="font-size:0.75rem;opacity:0.8;margin-left:6px;color:#f59e0b;" title="Message révisé">✎ Révisé</span>`
        : "";
      return `
        <div class="${bubbleClass}">
          <div class="chat-meta">
            <span class="chat-sender">${escapeHtml(senderLabel)}</span>
            <span class="chat-time">${escapeHtml(formatTimestamp(msg.created_at))}${revisedBadge}</span>
          </div>
          <div class="chat-body">${escapeHtml(msg.body)}</div>
        </div>
      `;
    })
    .join("");

  container.scrollTop = container.scrollHeight;
}

function renderOperatorAttachments(attachments: AttachmentRecord[]) {
  const container = document.getElementById("operator-attachments-list");
  if (!container) return;

  if (attachments.length === 0) {
    container.innerHTML = `
      <div class="empty-state">
        <i class="ph ph-paperclip"></i>
        <div class="empty-state-subtitle">Aucun fichier joint à ce ticket.</div>
      </div>
    `;
    return;
  }

  container.innerHTML = attachments
    .map((att) => {
      const isMe = att.sender_peer_id === currentPeerId;
      const sender = isMe ? "Envoyé par vous" : `Reçu de ${att.sender_peer_id.substring(0, 8)}`;
      return `
        <div class="attachment-item">
          <div class="attachment-icon"><i class="ph ph-file"></i></div>
          <div class="attachment-info">
            <div class="attachment-name" title="${escapeHtml(att.filename)}">${escapeHtml(att.filename)}</div>
            <div class="attachment-meta">
              <span>${formatBytes(att.size_bytes)}</span>
              <span>•</span>
              <span>${escapeHtml(sender)}</span>
              <span>•</span>
              <span class="code" title="SHA-256: ${escapeHtml(att.sha256)}">${escapeHtml(att.sha256.substring(0, 10))}...</span>
            </div>
          </div>
          <span class="badge ${att.state === 'READY' ? 'online' : ''}">${escapeHtml(att.state)}</span>
        </div>
      `;
    })
    .join("");
}

function renderOperatorEvents(events: TicketEventRecord[]) {
  const container = document.getElementById("operator-events-timeline");
  if (!container) return;

  if (events.length === 0) {
    container.innerHTML = `
      <div class="empty-state">
        <i class="ph ph-clock-counter-clockwise"></i>
        <div class="empty-state-subtitle">Aucun événement dans le journal.</div>
      </div>
    `;
    return;
  }

  container.innerHTML = events
    .map((ev) => {
      let icon = "ph-activity";
      if (ev.kind.includes("CREATED")) icon = "ph-plus-circle";
      else if (ev.kind.includes("ACCESS")) icon = "ph-shield-check";
      else if (ev.kind.includes("CLOSED")) icon = "ph-check-circle";
      else if (ev.kind.includes("MESSAGE")) icon = "ph-chat-circle";
      else if (ev.kind.includes("FILE")) icon = "ph-file";
      else if (ev.kind.includes("SHELL")) icon = "ph-terminal";

      return `
        <div class="event-item">
          <div class="event-icon"><i class="ph ${icon}"></i></div>
          <div class="event-content">
            <div class="event-header">
              <span class="event-kind">${escapeHtml(ev.kind)}</span>
              <span class="event-time">${escapeHtml(formatTimestamp(ev.timestamp))}</span>
            </div>
            <div class="event-actor code">Acteur: ${escapeHtml(ev.actor_peer_id.substring(0, 14))}...</div>
            ${ev.metadata ? `<div class="event-meta">${escapeHtml(ev.metadata)}</div>` : ""}
          </div>
        </div>
      `;
    })
    .join("");
}

// ----------------------------------------------------------------------------
// Managed Customer Portal UI
// ----------------------------------------------------------------------------

function renderManagedTicketPortal(activeTicket: TicketRecord | null, isOnline: boolean) {
  managedActiveTicket = activeTicket;
  const managedClosed = document.getElementById("managed-state-closed");
  const managedOpen = document.getElementById("managed-state-open");
  const managedTicketId = document.getElementById("managed-ticket-id");
  const managedTicketState = document.getElementById("managed-ticket-state");
  const managedTitleDisplay = document.getElementById("managed-ticket-title-display");
  const managedDescDisplay = document.getElementById("managed-ticket-desc-display");
  const btnAccessToggle = document.getElementById("btn-managed-access-toggle") as HTMLButtonElement | null;
  const btnCreate = document.getElementById("btn-managed-create") as HTMLButtonElement | null;

  if (btnCreate) btnCreate.disabled = !isOnline;

  if (activeTicket && isOnline) {
    if (managedClosed) managedClosed.style.display = "none";
    if (managedOpen) managedOpen.style.display = "flex";

    if (managedTicketId) managedTicketId.textContent = activeTicket.id;
    if (managedTicketState) {
      managedTicketState.textContent = activeTicket.state;
      managedTicketState.className = `ticket-badge ${activeTicket.state === 'OPEN' ? 'open' : 'active'}`;
    }
    if (managedTitleDisplay) managedTitleDisplay.textContent = activeTicket.title;
    if (managedDescDisplay) {
      managedDescDisplay.textContent = activeTicket.description || "Aucune description fournie.";
    }

    if (btnAccessToggle) {
      if (activeTicket.remote_access_enabled) {
        btnAccessToggle.textContent = "AUTORISÉ (Cliquer pour révoquer)";
        btnAccessToggle.className = "btn btn-secondary btn-sm";
      } else {
        btnAccessToggle.textContent = "RÉVOQUÉ (Cliquer pour autoriser)";
        btnAccessToggle.className = "btn btn-danger btn-sm";
      }
    }

    loadManagedTicketSubData(activeTicket.id);
  } else {
    if (managedClosed) managedClosed.style.display = "flex";
    if (managedOpen) managedOpen.style.display = "none";
  }
}

async function loadManagedTicketSubData(ticketId: string) {
  try {
    const detail = await invoke<TicketDetail | null>("get_ticket", { ticketId });
    if (!detail) return;
    renderClientChat(detail.messages);
    renderClientAttachments(detail.attachments);
  } catch (err) {
    console.error("Failed to load managed ticket sub data:", err);
  }
}

function renderClientChat(messages: ChatMessage[]) {
  const container = document.getElementById("client-chat-messages");
  if (!container) return;

  if (messages.length === 0) {
    container.innerHTML = `
      <div class="empty-state">
        <div class="empty-state-subtitle">Envoyez un message pour échanger avec le technicien.</div>
      </div>
    `;
    return;
  }

  container.innerHTML = messages
    .map((msg) => {
      const isMe = msg.sender_peer_id === currentPeerId;
      const bubbleClass = isMe ? "chat-bubble outgoing" : "chat-bubble incoming";
      const senderLabel = isMe ? "Vous" : "Technicien Opérateur";
      const revisedBadge = (msg.revised_at || (msg.revision_count && msg.revision_count > 0))
        ? `<span class="chat-revised" style="font-size:0.75rem;opacity:0.8;margin-left:6px;color:#f59e0b;" title="Message révisé">✎ Révisé</span>`
        : "";
      return `
        <div class="${bubbleClass}">
          <div class="chat-meta">
            <span class="chat-sender">${escapeHtml(senderLabel)}</span>
            <span class="chat-time">${escapeHtml(formatTimestamp(msg.created_at))}${revisedBadge}</span>
          </div>
          <div class="chat-body">${escapeHtml(msg.body)}</div>
        </div>
      `;
    })
    .join("");

  container.scrollTop = container.scrollHeight;
}

function renderClientAttachments(attachments: AttachmentRecord[]) {
  const container = document.getElementById("client-attachments-list");
  if (!container) return;

  if (attachments.length === 0) {
    container.innerHTML = `
      <div class="empty-state">
        <div class="empty-state-subtitle">Aucun fichier partagé pour le moment.</div>
      </div>
    `;
    return;
  }

  container.innerHTML = attachments
    .map((att) => {
      const isMe = att.sender_peer_id === currentPeerId;
      const sender = isMe ? "Envoyé par vous" : "Envoyé par le technicien";
      return `
        <div class="attachment-item">
          <div class="attachment-icon"><i class="ph ph-file"></i></div>
          <div class="attachment-info">
            <div class="attachment-name">${escapeHtml(att.filename)}</div>
            <div class="attachment-meta">
              <span>${formatBytes(att.size_bytes)}</span>
              <span>•</span>
              <span>${escapeHtml(sender)}</span>
            </div>
          </div>
          <span class="badge ${att.state === 'READY' ? 'online' : ''}">${escapeHtml(att.state)}</span>
        </div>
      `;
    })
    .join("");
}

// ----------------------------------------------------------------------------
// P2P Technical Network Peers Rendering
// ----------------------------------------------------------------------------

function renderNetworkPeers(peers: DesktopPeer[], isOnline: boolean) {
  const container = document.getElementById("network-peer-list");
  const countBadge = document.getElementById("network-count-badge");
  const serviceState = document.getElementById("diag-service-state");
  const connectedCount = document.getElementById("diag-connected-count");
  const relayCount = document.getElementById("diag-relay-count");
  const localPeer = document.getElementById("diag-local-peer");
  if (!container) return;

  const connected = peers.filter((peer) => isPeerConnected(peer.status)).length;
  const relays = peers.filter((peer) => peer.relay).length;
  if (serviceState) {
    serviceState.textContent = isOnline ? "En ligne" : "Hors-ligne";
    serviceState.className = isOnline ? "online" : "offline";
  }
  if (connectedCount) connectedCount.textContent = String(connected);
  if (relayCount) relayCount.textContent = String(relays);
  if (localPeer) localPeer.textContent = currentPeerId || "—";

  if (countBadge) {
    countBadge.textContent = `${peers.length} Pair${peers.length > 1 ? "s" : ""}`;
    countBadge.className = peers.length > 0 ? "badge open" : "badge";
  }

  if (!isOnline || peers.length === 0) {
    container.innerHTML = `<div class="empty-state network-empty">
      <i class="ph ph-network-slash"></i>
      <div class="empty-state-title">${isOnline ? "Aucun pair connu" : "Service déconnecté"}</div>
      <div class="empty-state-subtitle">${isOnline ? "Les pairs apparaîtront après leur découverte P2P." : "Le service FORTIQ doit être actif."}</div>
    </div>`;
    return;
  }

  container.innerHTML = peers
    .map((peer) => {
      const connected = isPeerConnected(peer.status);
      return `<article class="network-peer-card">
      <div class="network-peer-heading">
        <i class="ph ph-desktop-tower"></i>
        <div><strong>${escapeHtml(peer.hostname || "Pair inconnu")}</strong><span>${escapeHtml(peer.os || "OS inconnu")}</span></div>
        <span class="ticket-badge ${connected ? "open" : ""}">${connected ? "CONNECTÉ" : escapeHtml(peer.status.toUpperCase())}</span>
      </div>
      <dl>
        <div><dt>Peer ID</dt><dd class="code" title="${escapeHtml(peer.peerId)}">${escapeHtml(peer.peerId)}</dd></div>
        <div><dt>Transport</dt><dd>${escapeHtml(peer.transport || "P2P")}</dd></div>
        <div><dt>Rôle</dt><dd>${escapeHtml(peer.mode || "INCONNU")}</dd></div>
      </dl>
    </article>`;
    })
    .join("");
}

function renderOperatorSettings(status: DesktopStatus, isOnline: boolean) {
  const version = document.getElementById("settings-version");
  const peerId = document.getElementById("settings-peer-id");
  const serviceState = document.getElementById("settings-service-state");
  if (version) version.textContent = status.version || "—";
  if (peerId) peerId.textContent = status.peerId || "—";
  if (serviceState) {
    serviceState.textContent = isOnline ? "En ligne" : "Hors-ligne";
    serviceState.className = isOnline ? "online" : "offline";
  }
}

function scheduleRefresh(intervalMs: number) {
  if (refreshTimer !== null) window.clearInterval(refreshTimer);
  refreshTimer = window.setInterval(refresh, intervalMs);
}

function emitOperatorSnapshot(tickets: TicketRecord[], peers: DesktopPeer[]) {
  window.dispatchEvent(
    new CustomEvent("fortiq:operator-snapshot", {
      detail: { tickets, peers, peerId: currentPeerId },
    }),
  );
}

// ----------------------------------------------------------------------------
// Polling / State Refresh
// ----------------------------------------------------------------------------

async function refresh() {
  try {
    const status = await invoke<DesktopStatus>("desktop_status");
    const isOnline = status.agentState === "online";
    currentPeerId = status.peerId;
    updateStatusBadge(isOnline, isOnline ? "En Ligne (P2P)" : "Service Hors-Ligne");
    renderOperatorSettings(status, isOnline);

    if (isOnline) {
      const mode = status.mode.toLowerCase() === "managed" ? "managed" : "operator";
      applyMode(mode, true);

      if (mode === "managed") {
        if (status.activeTicketId) {
          const detail = await invoke<TicketDetail | null>("get_ticket", {
            ticketId: status.activeTicketId,
          });
          renderManagedTicketPortal(detail ? detail.ticket : null, true);
        } else {
          renderManagedTicketPortal(null, true);
        }
      } else {
        // Operator mode: fetch tickets and peers
        try {
          const tickets = await invoke<TicketRecord[]>("list_tickets", {});
          ticketsCache = tickets;
          renderTicketList(tickets, true);
        } catch (err) {
          console.warn("Failed to fetch tickets:", err);
          renderTicketList([], true);
        }

        let peers: DesktopPeer[] = [];
        try {
          peers = await invoke<DesktopPeer[]>("list_peers");
          renderNetworkPeers(peers, true);
        } catch (err) {
          console.warn("Failed to fetch network peers:", err);
          renderNetworkPeers([], true);
        }
        emitOperatorSnapshot(ticketsCache, peers);
      }
    } else {
      applyMode(status.mode === "managed" ? "managed" : "operator", false);
      renderTicketList([], false);
      renderManagedTicketPortal(null, false);
      renderNetworkPeers([], false);
      emitOperatorSnapshot([], []);
    }
  } catch (err) {
    console.warn("Daemon unreachable:", err);
    updateStatusBadge(false, "Service Hors-Ligne");
    applyMode("operator", false);
    renderTicketList([], false);
    renderManagedTicketPortal(null, false);
    renderNetworkPeers([], false);
    emitOperatorSnapshot([], []);
  }
}

// ----------------------------------------------------------------------------
// Terminal Management
// ----------------------------------------------------------------------------

function initTerminal() {
  const container = document.getElementById("xterm-container");
  if (!container) return;

  term = new Terminal({
    cursorBlink: true,
    fontFamily: '"Cascadia Code", "JetBrains Mono", Consolas, monospace',
    fontSize: 13,
    lineHeight: 1.2,
    theme: {
      background: "#070a10",
      foreground: "#e6edf3",
      cursor: "#00d2ff",
      selectionBackground: "rgba(0, 132, 255, 0.3)",
      black: "#070a10",
      brightBlack: "#5e6b7d",
      red: "#f85149",
      brightRed: "#ff7b72",
      green: "#2ea043",
      brightGreen: "#7ee787",
      yellow: "#e3b341",
      brightYellow: "#f2cc60",
      blue: "#0084ff",
      brightBlue: "#58a6ff",
      magenta: "#bc8cff",
      brightMagenta: "#d2a8ff",
      cyan: "#00d2ff",
      brightCyan: "#56d4dd",
      white: "#b1bac4",
      brightWhite: "#ffffff",
    },
    convertEol: true,
  });

  fitAddon = new FitAddon();
  term.loadAddon(fitAddon);
  term.open(container);

  term.onData((data) => {
    if (isTerminalActive) {
      invoke("write_terminal_data", { data }).catch((err) => {
        console.error("write_terminal_data error:", err);
      });
    }
  });

  term.onResize(({ cols, rows }) => {
    if (isTerminalActive) {
      invoke("resize_terminal", { cols, rows }).catch((err) => {
        console.error("resize_terminal error:", err);
      });
    }
  });

  const ro = new ResizeObserver(() => {
    if (container.style.display !== "none" && fitAddon) {
      try {
        fitAddon.fit();
      } catch (e) {
        // ignore during initial layout
      }
    }
  });
  ro.observe(container);

  listen<string>("terminal-output", (event) => {
    if (term) {
      term.write(event.payload);
    }
  });

  listen("terminal-closed", () => {
    isTerminalActive = false;
    terminalTicketId = null;
    const container = document.getElementById("xterm-container");
    const placeholder = document.getElementById("terminal-placeholder");
    const termDot = document.getElementById("terminal-dot");
    const termTitle = document.getElementById("terminal-title-text");
    const btnToggle = document.getElementById("btn-terminal-toggle") as HTMLButtonElement | null;
    const btnRevoke = document.getElementById("btn-terminal-revoke") as HTMLButtonElement | null;
    if (termDot) termDot.className = "status-dot";
    if (termTitle) termTitle.textContent = "Shell du ticket — Session terminée";
    if (btnToggle) btnToggle.textContent = "Démarrer le shell";
    if (btnRevoke) btnRevoke.disabled = true;
    if (container) container.style.display = "none";
    if (placeholder) placeholder.style.display = "flex";
    if (term) {
      term.write("\r\n\x1b[33m[FORTIQ] Session terminal fermée.\x1b[0m\r\n");
    }
  });
}

function queueTerminalSession(peerId: string, ticketId?: string | null) {
  const generation = ++terminalSwitchGeneration;
  terminalSwitchQueue = terminalSwitchQueue
    .catch(() => undefined)
    .then(() => connectTerminalSession(peerId, ticketId, generation));
}

async function connectTerminalSession(peerId: string, ticketId: string | null | undefined, generation: number) {
  const container = document.getElementById("xterm-container");
  const placeholder = document.getElementById("terminal-placeholder");
  const btnToggle = document.getElementById("btn-terminal-toggle") as HTMLButtonElement | null;
  const btnRevoke = document.getElementById("btn-terminal-revoke") as HTMLButtonElement | null;
  const termDot = document.getElementById("terminal-dot");
  const termTitle = document.getElementById("terminal-title-text");
  const deniedBanner = document.getElementById("terminal-denied-banner");
  const deniedMsg = document.getElementById("terminal-denied-msg");

  if (deniedBanner) deniedBanner.style.display = "none";
  if (placeholder) placeholder.style.display = "none";
  if (container) container.style.display = "block";

  if (fitAddon) {
    try {
      fitAddon.fit();
    } catch (e) {
      // ignore
    }
  }

  const cols = term?.cols || 80;
  const rows = term?.rows || 24;

  if (term) {
    term.reset();
    term.write(
      `\x1b[1;36m[FORTIQ]\x1b[0m Établissement de la session ConPTY/PTY sur ticket ${ticketId || "sans ticket"} vers ${peerId}...\r\n`
    );
  }

  if (btnToggle) {
    btnToggle.disabled = true;
    btnToggle.innerHTML = `<i class="ph ph-spinner"></i><span>Connexion...</span>`;
  }

  try {
    await invoke("start_terminal_session", {
      peer: peerId,
      ticketId: ticketId || null,
      cols,
      rows,
    });
    if (generation !== terminalSwitchGeneration) return;
    isTerminalActive = true;
    terminalTicketId = ticketId || null;
    if (termDot) termDot.className = "status-dot online";
    if (termTitle) termTitle.textContent = `Shell actif — ticket ${(ticketId || "").substring(0, 8)}`;
    if (btnToggle) btnToggle.textContent = "Fermer le shell";
    if (btnRevoke) btnRevoke.disabled = false;
  } catch (err: any) {
    if (generation !== terminalSwitchGeneration) return;
    isTerminalActive = false;
    terminalTicketId = null;
    if (btnRevoke) btnRevoke.disabled = true;
    const errMsg = String(err);
    if (term) {
      term.write(`\r\n\x1b[1;31m[ACCÈS REFUSÉ]\x1b[0m ${errMsg}\r\n`);
    }
    if (deniedBanner) {
      deniedBanner.style.display = "flex";
      if (deniedMsg) {
        if (errMsg.includes("DENIED_REMOTE_ACCESS_DISABLED") || errMsg.includes("remote access")) {
          deniedMsg.textContent = "Accès refusé : Le client a désactivé la télé-assistance sur ce ticket (DENIED_REMOTE_ACCESS_DISABLED).";
        } else if (errMsg.includes("DENIED_TICKET_CLOSED") || errMsg.includes("ticket is closed")) {
          deniedMsg.textContent = "Accès refusé : Le ticket d'assistance est clôturé (DENIED_TICKET_CLOSED).";
        } else {
          deniedMsg.textContent = `Accès refusé : ${errMsg}`;
        }
      }
    }
  } finally {
    if (btnToggle) {
      btnToggle.disabled = false;
      if (!isTerminalActive) {
        btnToggle.textContent = "Démarrer le shell";
      }
    }
    if (btnRevoke && !isTerminalActive) {
      btnRevoke.disabled = true;
    }
  }
}

// ----------------------------------------------------------------------------
// Canonical Architecture: Self-Support Loop & Portable Operator
// ----------------------------------------------------------------------------

async function loadSelfSupportDiagnostics() {
  try {
    const diag = await invoke<SelfSupportDiagnosticsDto>("get_self_support_diagnostics");
    const osEl = document.getElementById("self-diag-os");
    const archEl = document.getElementById("self-diag-arch");
    const hostEl = document.getElementById("self-diag-hostname");
    const loopEl = document.getElementById("self-diag-loopback");
    const relayEl = document.getElementById("self-diag-relay-bypassed");
    const shardsEl = document.getElementById("self-diag-shards");
    const eventsEl = document.getElementById("self-diag-event-packs");
    const headsEl = document.getElementById("self-diag-canonical-heads");

    if (osEl) osEl.textContent = diag.os;
    if (archEl) archEl.textContent = diag.arch;
    if (hostEl) hostEl.textContent = diag.hostname;
    if (loopEl) loopEl.textContent = diag.is_loopback_active ? "Actif (Local)" : "Inactif";
    if (relayEl) relayEl.textContent = diag.relay_bypassed ? "Oui (100% Hors-ligne)" : "Non";
    if (shardsEl) shardsEl.textContent = String(diag.active_shards);
    if (eventsEl) eventsEl.textContent = String(diag.event_packs_stored);
    if (headsEl) headsEl.textContent = String(diag.canonical_heads);
  } catch (err) {
    console.error("Échec chargement diagnostics auto-support:", err);
  }
}

async function handleCreateSelfSupportTicket() {
  const titleInput = document.getElementById("self-support-ticket-title") as HTMLInputElement | null;
  const descInput = document.getElementById("self-support-ticket-desc") as HTMLTextAreaElement | null;
  const resultBanner = document.getElementById("self-support-ticket-result");
  const resultMsg = document.getElementById("self-support-ticket-msg");

  const title = titleInput?.value.trim() || "";
  const description = descInput?.value.trim() || "";
  if (!title) {
    alert("Veuillez indiquer un titre pour le diagnostic d'auto-support.");
    return;
  }

  try {
    const ticket = await invoke<SelfSupportTicketDto>("create_self_support_ticket", {
      title,
      description,
    });
    if (resultBanner && resultMsg) {
      resultBanner.style.display = "flex";
      resultBanner.style.background = "rgba(34, 197, 94, 0.15)";
      resultBanner.style.borderColor = "rgba(34, 197, 94, 0.3)";
      resultBanner.style.color = "#22c55e";
      resultMsg.innerHTML = `Ticket auto-support créé : <strong>${escapeHtml(ticket.ticket_id.substring(0, 16))}...</strong> (AccessEpoch : ${escapeHtml(ticket.access_epoch.substring(0, 16))}...)`;
    }
    if (titleInput) titleInput.value = "";
    if (descInput) descInput.value = "";
    loadSelfSupportDiagnostics();
  } catch (err) {
    if (resultBanner && resultMsg) {
      resultBanner.style.display = "flex";
      resultBanner.style.background = "rgba(239, 68, 68, 0.15)";
      resultBanner.style.borderColor = "rgba(239, 68, 68, 0.3)";
      resultBanner.style.color = "#ef4444";
      resultMsg.textContent = `Erreur : ${String(err)}`;
    }
  }
}

async function handleUnlockMnemonic() {
  const input = document.getElementById("mnemonic-input") as HTMLTextAreaElement | null;
  const badge = document.getElementById("operator-session-badge");
  const details = document.getElementById("operator-session-details");
  const entityEl = document.getElementById("operator-session-entity");
  const capsEl = document.getElementById("operator-session-caps");
  const expiresEl = document.getElementById("operator-session-expires");
  const statusMsg = document.getElementById("mnemonic-status-msg");
  const btnUnlock = document.getElementById("btn-unlock-mnemonic") as HTMLButtonElement | null;
  const btnLock = document.getElementById("btn-lock-mnemonic") as HTMLButtonElement | null;

  const words = input?.value.trim() || "";
  if (!words) {
    if (statusMsg) {
      statusMsg.style.color = "#ef4444";
      statusMsg.textContent = "Veuillez entrer une phrase mnémonique valide (ex. 24 mots).";
    }
    return;
  }

  try {
    const session = await invoke<OperatorSessionDto>("unlock_portable_operator", {
      mnemonicWords: words,
    });
    if (badge) {
      badge.className = "badge success";
      badge.textContent = "Déverrouillé";
    }
    if (details) details.style.display = "block";
    if (entityEl) entityEl.textContent = session.operator_entity;
    if (capsEl) capsEl.textContent = session.capabilities.join(", ");
    if (expiresEl) expiresEl.textContent = formatTimestamp(session.expires_at);
    if (btnLock) btnLock.disabled = false;
    if (btnUnlock) btnUnlock.disabled = true;
    if (input) input.value = "";
    if (statusMsg) {
      statusMsg.style.color = "#22c55e";
      statusMsg.textContent = "Session opérateur déverrouillée avec succès en mémoire.";
    }
  } catch (err) {
    if (statusMsg) {
      statusMsg.style.color = "#ef4444";
      statusMsg.textContent = `Échec de déverrouillage : ${String(err)}`;
    }
  }
}

async function handleLockMnemonic() {
  const badge = document.getElementById("operator-session-badge");
  const details = document.getElementById("operator-session-details");
  const statusMsg = document.getElementById("mnemonic-status-msg");
  const btnUnlock = document.getElementById("btn-unlock-mnemonic") as HTMLButtonElement | null;
  const btnLock = document.getElementById("btn-lock-mnemonic") as HTMLButtonElement | null;

  try {
    await invoke("lock_portable_operator");
    if (badge) {
      badge.className = "badge";
      badge.textContent = "Verrouillé";
    }
    if (details) details.style.display = "none";
    if (btnLock) btnLock.disabled = true;
    if (btnUnlock) btnUnlock.disabled = false;
    if (statusMsg) {
      statusMsg.style.color = "#94a3b8";
      statusMsg.textContent = "Session opérateur verrouillée et mémoire purgée.";
    }
  } catch (err) {
    console.error("Échec verrouillage opérateur:", err);
  }
}

// ----------------------------------------------------------------------------
// Event Listeners Initialization
// ----------------------------------------------------------------------------

function initEventListeners() {
  // Operator navigation
  document.querySelectorAll<HTMLElement>(".nav-item").forEach((btn) => {
    btn.addEventListener("click", () => {
      const tab = btn.dataset.tab;
      if (tab === "tickets" || tab === "self-support" || tab === "peers" || tab === "settings") {
        setOperatorTab(tab);
      }
    });
  });

  const btnNetworkRefresh = document.getElementById("btn-network-refresh") as HTMLButtonElement | null;
  if (btnNetworkRefresh) {
    btnNetworkRefresh.addEventListener("click", async () => {
      btnNetworkRefresh.disabled = true;
      await refresh();
      btnNetworkRefresh.disabled = false;
    });
  }

  const refreshSelect = document.getElementById("settings-refresh-interval") as HTMLSelectElement | null;
  if (refreshSelect) {
    const savedInterval = localStorage.getItem("fortiq.refreshInterval") || "2500";
    refreshSelect.value = savedInterval;
    refreshSelect.addEventListener("change", () => {
      const intervalMs = Number(refreshSelect.value);
      localStorage.setItem("fortiq.refreshInterval", String(intervalMs));
      scheduleRefresh(intervalMs);
    });
  }

  // Ticket sub-tabs (overview, chat, files, events)
  document.querySelectorAll<HTMLElement>(".ticket-tab-btn[data-ttab]").forEach((btn) => {
    btn.addEventListener("click", () => {
      const ttab = btn.dataset.ttab as "overview" | "chat" | "files" | "events" | "terminal";
      if (ttab) setTicketSubTab(ttab);
    });
  });

  // Client sub-tabs (chat, files)
  document.querySelectorAll<HTMLElement>(".ticket-tab-btn[data-mttab]").forEach((btn) => {
    btn.addEventListener("click", () => {
      const mttab = btn.dataset.mttab as "chat" | "files";
      if (mttab) setClientSubTab(mttab);
    });
  });

  // Ticket filter bar
  document.querySelectorAll<HTMLElement>(".filter-btn").forEach((btn) => {
    btn.addEventListener("click", () => {
      document.querySelectorAll(".filter-btn").forEach((b) => b.classList.remove("active"));
      btn.classList.add("active");
      const f = btn.dataset.filter as any;
      ticketFilter = f || "ALL";
      renderTicketList(ticketsCache, true);
    });
  });

  // Overview action buttons
  const btnTake = document.getElementById("btn-ticket-take");
  if (btnTake) {
    btnTake.addEventListener("click", async () => {
      if (!selectedTicketId) return;
      try {
        await invoke("update_ticket_status", { ticketId: selectedTicketId, state: "IN_PROGRESS" });
        await refresh();
      } catch (err) {
        alert(`Erreur lors de la prise en charge : ${err}`);
      }
    });
  }

  const btnResolve = document.getElementById("btn-ticket-resolve");
  if (btnResolve) {
    btnResolve.addEventListener("click", async () => {
      if (!selectedTicketId) return;
      try {
        await invoke("update_ticket_status", { ticketId: selectedTicketId, state: "RESOLVED" });
        await refresh();
      } catch (err) {
        alert(`Erreur lors du marquage résolu : ${err}`);
      }
    });
  }

  const btnClose = document.getElementById("btn-close-ticket");
  if (btnClose) {
    btnClose.addEventListener("click", async () => {
      if (!selectedTicketId) return;
      if (!confirm("Voulez-vous vraiment clôturer définitivement ce ticket ?")) return;
      try {
        await invoke("update_ticket_status", { ticketId: selectedTicketId, state: "CLOSED" });
        await refresh();
      } catch (err) {
        alert(`Erreur lors de la clôture : ${err}`);
      }
    });
  }

  // Operator Chat Send
  const btnOpChatSend = document.getElementById("btn-operator-chat-send");
  const opChatInput = document.getElementById("operator-chat-input") as HTMLInputElement | null;
  const sendOpChat = async () => {
    if (!selectedTicketId || !opChatInput) return;
    const body = opChatInput.value.trim();
    if (!body) return;
    try {
      opChatInput.value = "";
      await invoke("send_chat_message", { ticketId: selectedTicketId, body });
      await loadSelectedTicketDetail(selectedTicketId);
    } catch (err) {
      alert(`Erreur d'envoi du message : ${err}`);
    }
  };
  if (btnOpChatSend) btnOpChatSend.addEventListener("click", sendOpChat);
  if (opChatInput) {
    opChatInput.addEventListener("keydown", (e) => {
      if (e.key === "Enter") {
        e.preventDefault();
        sendOpChat();
      }
    });
  }

  // Operator File Send
  const btnOpFileSend = document.getElementById("btn-operator-file-send");
  const opFilePathInput = document.getElementById("operator-file-path") as HTMLInputElement | null;
  const sendOpFile = async () => {
    if (!selectedTicketId || !opFilePathInput) return;
    const filePath = opFilePathInput.value.trim();
    if (!filePath) return;
    try {
      opFilePathInput.value = "";
      await invoke("send_file", { ticketId: selectedTicketId, filePath });
      await loadSelectedTicketDetail(selectedTicketId);
    } catch (err) {
      alert(`Erreur d'envoi du fichier : ${err}`);
    }
  };
  if (btnOpFileSend) btnOpFileSend.addEventListener("click", sendOpFile);
  if (opFilePathInput) {
    opFilePathInput.addEventListener("keydown", (e) => {
      if (e.key === "Enter") {
        e.preventDefault();
        sendOpFile();
      }
    });
  }

  // Terminal Toggle Button
  const btnTermToggle = document.getElementById("btn-terminal-toggle");
  if (btnTermToggle) {
    btnTermToggle.addEventListener("click", () => {
      if (!currentTicketDetail) return;
      const ticket = currentTicketDetail.ticket;
      if (isTerminalActive && terminalTicketId === ticket.id) {
        invoke("close_terminal_session").catch((err) => console.error("close_terminal_session error:", err));
        return;
      }
      queueTerminalSession(ticket.client_peer_id, ticket.id);
    });
  }

  // Emergency Revoke Button (Immediate safety gate revocation)
  const btnTermRevoke = document.getElementById("btn-terminal-revoke");
  if (btnTermRevoke) {
    btnTermRevoke.addEventListener("click", async () => {
      const ticketId = terminalTicketId || selectedTicketId || "";
      if (!ticketId) return;
      try {
        await invoke("revoke_active_shell", { ticketId });
        if (term) {
          term.write("\r\n\x1b[1;31m[FORTIQ]\x1b[0m Révocation d'urgence immédiate exécutée.\r\n");
        }
        await invoke("close_terminal_session").catch(() => {});
      } catch (err) {
        console.error("Échec révocation d'urgence:", err);
      }
    });
  }

  // Clear & Fullscreen terminal
  const btnTermClear = document.getElementById("btn-term-clear");
  if (btnTermClear) {
    btnTermClear.addEventListener("click", () => {
      if (term) term.clear();
    });
  }

  const btnTermFullscreen = document.getElementById("btn-term-fullscreen");
  const panelTerminal = document.querySelector(".ticket-terminal");
  if (btnTermFullscreen && panelTerminal) {
    btnTermFullscreen.addEventListener("click", () => {
      panelTerminal.classList.toggle("fullscreen");
      setTimeout(() => {
        if (fitAddon) fitAddon.fit();
      }, 100);
    });
  }

  // Self-Support Controls
  const btnSelfRefresh = document.getElementById("btn-self-support-refresh");
  if (btnSelfRefresh) {
    btnSelfRefresh.addEventListener("click", () => {
      loadSelfSupportDiagnostics();
    });
  }

  const btnCreateSelfTicket = document.getElementById("btn-create-self-ticket");
  if (btnCreateSelfTicket) {
    btnCreateSelfTicket.addEventListener("click", () => {
      handleCreateSelfSupportTicket();
    });
  }

  // Portable Operator Mnemonic Controls
  const btnUnlockMnemonic = document.getElementById("btn-unlock-mnemonic");
  if (btnUnlockMnemonic) {
    btnUnlockMnemonic.addEventListener("click", () => {
      handleUnlockMnemonic();
    });
  }

  const btnLockMnemonic = document.getElementById("btn-lock-mnemonic");
  if (btnLockMnemonic) {
    btnLockMnemonic.addEventListener("click", () => {
      handleLockMnemonic();
    });
  }

  // Managed view: Create Ticket Form
  const btnManagedCreate = document.getElementById("btn-managed-create") as HTMLButtonElement | null;
  const managedTitleInput = document.getElementById("managed-input-title") as HTMLInputElement | null;
  const managedDescInput = document.getElementById("managed-input-desc") as HTMLTextAreaElement | null;
  const managedPrioSelect = document.getElementById("managed-select-prio") as HTMLSelectElement | null;

  if (btnManagedCreate) {
    btnManagedCreate.addEventListener("click", async () => {
      if (!managedTitleInput) return;
      const title = managedTitleInput.value.trim();
      if (!title) {
        alert("Veuillez indiquer un objet pour la demande.");
        return;
      }
      const description = managedDescInput?.value.trim() || "";
      const priority = managedPrioSelect?.value || "NORMAL";

      try {
        btnManagedCreate.disabled = true;
        await invoke("create_ticket", { title, description, priority });
        managedTitleInput.value = "";
        if (managedDescInput) managedDescInput.value = "";
        await refresh();
      } catch (err) {
        alert(`Échec de création du ticket : ${err}`);
      } finally {
        btnManagedCreate.disabled = false;
      }
    });
  }

  // Managed view: Close Ticket
  const btnManagedClose = document.getElementById("btn-managed-close");
  if (btnManagedClose) {
    btnManagedClose.addEventListener("click", async () => {
      if (!managedActiveTicket) return;
      if (!confirm("Voulez-vous clôturer cette demande d'assistance ?")) return;
      try {
        await invoke("update_ticket_status", {
          ticketId: managedActiveTicket.id,
          state: "CLOSED",
        });
        await refresh();
      } catch (err) {
        alert(`Erreur de clôture : ${err}`);
      }
    });
  }

  // Managed view: Remote Access Toggle
  const btnManagedAccessToggle = document.getElementById("btn-managed-access-toggle");
  if (btnManagedAccessToggle) {
    btnManagedAccessToggle.addEventListener("click", async () => {
      if (!managedActiveTicket) return;
      const newEnabled = !managedActiveTicket.remote_access_enabled;
      try {
        await invoke("set_remote_access", {
          ticketId: managedActiveTicket.id,
          enabled: newEnabled,
        });
        await refresh();
      } catch (err) {
        alert(`Erreur de modification d'accès à distance : ${err}`);
      }
    });
  }

  // Managed view: Chat Send
  const btnClientChatSend = document.getElementById("btn-client-chat-send");
  const clientChatInput = document.getElementById("client-chat-input") as HTMLInputElement | null;
  const sendClientChat = async () => {
    if (!managedActiveTicket || !clientChatInput) return;
    const body = clientChatInput.value.trim();
    if (!body) return;
    try {
      clientChatInput.value = "";
      await invoke("send_chat_message", { ticketId: managedActiveTicket.id, body });
      await loadManagedTicketSubData(managedActiveTicket.id);
    } catch (err) {
      alert(`Erreur d'envoi du message : ${err}`);
    }
  };
  if (btnClientChatSend) btnClientChatSend.addEventListener("click", sendClientChat);
  if (clientChatInput) {
    clientChatInput.addEventListener("keydown", (e) => {
      if (e.key === "Enter") {
        e.preventDefault();
        sendClientChat();
      }
    });
  }

  // Managed view: File Send
  const btnClientFileSend = document.getElementById("btn-client-file-send");
  const clientFilePathInput = document.getElementById("client-file-path") as HTMLInputElement | null;
  const sendClientFile = async () => {
    if (!managedActiveTicket || !clientFilePathInput) return;
    const filePath = clientFilePathInput.value.trim();
    if (!filePath) return;
    try {
      clientFilePathInput.value = "";
      await invoke("send_file", { ticketId: managedActiveTicket.id, filePath });
      await loadManagedTicketSubData(managedActiveTicket.id);
    } catch (err) {
      alert(`Erreur d'envoi du fichier : ${err}`);
    }
  };
  if (btnClientFileSend) btnClientFileSend.addEventListener("click", sendClientFile);
  if (clientFilePathInput) {
    clientFilePathInput.addEventListener("keydown", (e) => {
      if (e.key === "Enter") {
        e.preventDefault();
        sendClientFile();
      }
    });
  }
}

window.addEventListener("DOMContentLoaded", () => {
  initTerminal();
  initEventListeners();
  refresh();
  const savedInterval = Number(localStorage.getItem("fortiq.refreshInterval") || "2500");
  scheduleRefresh(savedInterval);
});
