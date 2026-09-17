import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { open } from "@tauri-apps/plugin-dialog";
import { openPath, revealItemInDir } from "@tauri-apps/plugin-opener";
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
let activeOperatorTab: "tickets" | "peers" = "tickets";
let ticketFilter: "ALL" | "OPEN" | "IN_PROGRESS" | "RESOLVED" | "CLOSED" = "ALL";
let ticketSearch = "";
let ticketSort: "activity" | "priority" | "oldest" = "activity";

let ticketsCache: TicketRecord[] = [];
let selectedTicketId: string | null = null;
let currentTicketDetail: TicketDetail | null = null;

// Managed client active ticket cache
let managedActiveTicket: TicketRecord | null = null;

// Terminal State
let term: Terminal | null = null;
let fitAddon: FitAddon | null = null;
let isTerminalActive = false;
let terminalSwitchGeneration = 0;
let terminalSwitchQueue: Promise<void> = Promise.resolve();

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

function formatRelativeTime(epochSecs: number): string {
  const seconds = Math.max(0, Math.floor(Date.now() / 1000) - epochSecs);
  if (seconds < 60) return "à l'instant";
  if (seconds < 3600) return `il y a ${Math.floor(seconds / 60)} min`;
  if (seconds < 86400) return `il y a ${Math.floor(seconds / 3600)} h`;
  return `il y a ${Math.floor(seconds / 86400)} j`;
}

function showToast(title: string, message: string, kind: "info" | "error" = "info") {
  const region = document.getElementById("toast-region");
  if (!region) return;
  const toast = document.createElement("div");
  toast.className = `toast ${kind === "error" ? "toast--error" : ""}`;
  toast.innerHTML = `
    <i class="ph ${kind === "error" ? "ph-warning-circle" : "ph-check-circle"}"></i>
    <div><strong>${escapeHtml(title)}</strong><span>${escapeHtml(message)}</span></div>
    <button class="toast__close" aria-label="Fermer la notification"><i class="ph ph-x"></i></button>`;
  toast.querySelector("button")?.addEventListener("click", () => toast.remove());
  region.appendChild(toast);
  window.setTimeout(() => toast.remove(), kind === "error" ? 8000 : 4500);
}

function requestConfirmation(title: string, description: string, action: string): Promise<boolean> {
  const backdrop = document.getElementById("confirm-dialog") as HTMLElement | null;
  const titleEl = document.getElementById("confirm-title");
  const descriptionEl = document.getElementById("confirm-description");
  const cancel = document.getElementById("confirm-cancel") as HTMLButtonElement | null;
  const accept = document.getElementById("confirm-accept") as HTMLButtonElement | null;
  if (!backdrop || !cancel || !accept) return Promise.resolve(false);
  if (titleEl) titleEl.textContent = title;
  if (descriptionEl) descriptionEl.textContent = description;
  accept.textContent = action;
  backdrop.hidden = false;
  cancel.focus();
  return new Promise((resolve) => {
    const finish = (value: boolean) => {
      backdrop.hidden = true;
      cancel.removeEventListener("click", onCancel);
      accept.removeEventListener("click", onAccept);
      resolve(value);
    };
    const onCancel = () => finish(false);
    const onAccept = () => finish(true);
    cancel.addEventListener("click", onCancel);
    accept.addEventListener("click", onAccept);
  });
}

function setTerminalPanelVisible(visible: boolean) {
  const layout = document.getElementById("operator-view");
  const panel = document.querySelector<HTMLElement>(".panel-terminal");
  layout?.classList.toggle("terminal-open", visible);
  panel?.setAttribute("aria-hidden", String(!visible));
  if (visible) window.setTimeout(() => fitAddon?.fit(), 80);
}

async function chooseAndSendFile(ticketId: string): Promise<boolean> {
  const selected = await open({ multiple: false, directory: false });
  if (!selected || Array.isArray(selected)) return false;
  await invoke("send_file", { ticketId, filePath: selected });
  return true;
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
  const peersView = document.getElementById("peers-view");
  const managedView = document.getElementById("managed-view");
  const navMenu = document.querySelector(".nav-menu") as HTMLElement | null;
  const brandModeEl = document.getElementById("brand-mode");
  const userRoleEl = document.getElementById("user-role");

  if (mode === "operator") {
    if (operatorView) operatorView.style.display = activeOperatorTab === "tickets" ? "grid" : "none";
    if (peersView) peersView.style.display = activeOperatorTab === "peers" ? "grid" : "none";
    if (managedView) managedView.style.display = "none";
    if (navMenu) navMenu.style.display = "flex";
    if (brandModeEl) brandModeEl.textContent = "CONSOLE OPÉRATEUR";
    if (userRoleEl) userRoleEl.textContent = isOnline ? "OPÉRATEUR" : "DÉCONNECTÉ";
  } else {
    if (operatorView) operatorView.style.display = "none";
    if (peersView) peersView.style.display = "none";
    if (managedView) managedView.style.display = "flex";
    if (navMenu) navMenu.style.display = "none";
    if (brandModeEl) brandModeEl.textContent = "CLIENT MANAGÉ";
    if (userRoleEl) userRoleEl.textContent = isOnline ? "CLIENT MANAGÉ" : "DÉCONNECTÉ";
  }
}

function setOperatorTab(tab: "tickets" | "peers") {
  activeOperatorTab = tab;
  const operatorView = document.getElementById("operator-view");
  const peersView = document.getElementById("peers-view");
  if (operatorView) operatorView.style.display = tab === "tickets" ? "grid" : "none";
  if (peersView) peersView.style.display = tab === "peers" ? "grid" : "none";
  document.querySelectorAll<HTMLElement>(".nav-item").forEach((item) => {
    const selected = item.dataset.tab === tab;
    item.classList.toggle("active", selected);
    item.setAttribute("aria-selected", String(selected));
  });
}

function setTicketSubTab(tab: "overview" | "chat" | "files" | "events") {
  document.querySelectorAll<HTMLElement>(".ticket-tab-btn[data-ttab]").forEach((btn) => {
    const selected = btn.dataset.ttab === tab;
    btn.classList.toggle("active", selected);
    btn.setAttribute("aria-selected", String(selected));
  });
  const panes = ["overview", "chat", "files", "events"];
  panes.forEach((p) => {
    const paneEl = document.getElementById(`ticket-pane-${p}`);
    if (paneEl) paneEl.style.display = p === tab ? (p === "chat" || p === "files" ? "flex" : "block") : "none";
  });
}

function setClientSubTab(tab: "chat" | "files") {
  document.querySelectorAll<HTMLElement>(".ticket-tab-btn[data-mttab]").forEach((btn) => {
    const selected = btn.dataset.mttab === tab;
    btn.classList.toggle("active", selected);
    btn.setAttribute("aria-selected", String(selected));
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
      managedBeaconText.textContent = "Service disponible · Échanges sécurisés";
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
      managedBeaconText.textContent = "Service FORTIQ indisponible";
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

  const query = ticketSearch.trim().toLocaleLowerCase();
  const priorityWeight = { URGENT: 3, HIGH: 2, NORMAL: 1 } as const;
  const filtered = tickets
    .filter((t) => {
      if (ticketFilter !== "ALL" && t.state !== ticketFilter) return false;
      if (!query) return true;
      return [t.id, t.title, t.description, t.client_peer_id]
        .join(" ")
        .toLocaleLowerCase()
        .includes(query);
    })
    .sort((a, b) => {
      if (ticketSort === "priority") return priorityWeight[b.priority] - priorityWeight[a.priority] || b.updated_at - a.updated_at;
      if (ticketSort === "oldest") return a.created_at - b.created_at;
      return b.updated_at - a.updated_at;
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
        <span>${escapeHtml(ticket.client_peer_id.substring(0, 10))}… · ${escapeHtml(formatRelativeTime(ticket.updated_at))}</span>
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
  const btnFileSend = document.getElementById("btn-operator-file-send") as HTMLButtonElement | null;
  if (btnFileSend) btnFileSend.disabled = true;

  const eventsTimeline = document.getElementById("operator-events-timeline");
  if (eventsTimeline) {
    eventsTimeline.innerHTML = `<div class="empty-state"><div class="empty-state-subtitle">Sélectionnez un ticket pour afficher les événements.</div></div>`;
  }
}

async function loadSelectedTicketDetail(ticketId: string) {
  try {
    const detail = await invoke<TicketDetail | null>("get_ticket", { ticketId });
    if (!detail) {
      clearTicketDetails();
      return;
    }
    currentTicketDetail = detail;
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
    btnTermToggle.disabled = isClosed;
    btnTermToggle.textContent = isTerminalActive ? "Terminer la session" : "Démarrer Terminal";
  }

  if (chatInput) chatInput.disabled = isClosed;
  if (btnChatSend) btnChatSend.disabled = isClosed;
  if (btnFileSend) btnFileSend.disabled = isClosed;

  // Update terminal denied banner if remote access is off or closed
  const deniedBanner = document.getElementById("terminal-denied-banner");
  const deniedMsg = document.getElementById("terminal-denied-msg");
  if (isClosed) {
    if (deniedBanner) deniedBanner.style.display = "flex";
    if (deniedMsg) deniedMsg.textContent = "Accès refusé : Le ticket est clôturé (DENIED_TICKET_CLOSED).";
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
  const stayAtBottom = container.scrollHeight - container.scrollTop - container.clientHeight < 48;

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
      const bubbleClass = isMe ? "message message--mine" : "message message--remote";
      const senderLabel = isMe ? "Moi (Opérateur)" : `Client (${msg.sender_peer_id.substring(0, 8)})`;
      const state = msg.delivery_state || "PENDING";
      return `
        <div class="${bubbleClass}">
          <div class="message__meta">
            <span class="message__sender">${escapeHtml(senderLabel)}</span>
            <span class="message__time">${escapeHtml(formatTimestamp(msg.created_at))}</span>
          </div>
          <div class="message__body">${escapeHtml(msg.body)}</div>
          ${isMe ? `<span class="message__state ${state === "FAILED" ? "message__state--failed" : ""}">${escapeHtml(state === "DELIVERED" ? "Livré" : state === "FAILED" ? "Échec — réessayez" : "Envoi…")}</span>` : ""}
        </div>
      `;
    })
    .join("");

  if (stayAtBottom) container.scrollTop = container.scrollHeight;
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
          <div class="attachment-actions">
            <button class="btn-icon attachment-action" data-attachment-action="open" data-path="${escapeHtml(att.local_path)}" title="Ouvrir" aria-label="Ouvrir ${escapeHtml(att.filename)}"><i class="ph ph-arrow-square-out"></i></button>
            <button class="btn-icon attachment-action" data-attachment-action="reveal" data-path="${escapeHtml(att.local_path)}" title="Afficher dans le dossier" aria-label="Afficher ${escapeHtml(att.filename)} dans le dossier"><i class="ph ph-folder-open"></i></button>
            <button class="btn-icon attachment-action" data-attachment-action="hash" data-hash="${escapeHtml(att.sha256)}" title="Copier SHA-256" aria-label="Copier le SHA-256"><i class="ph ph-copy"></i></button>
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
  const consentCard = document.getElementById("managed-consent-card");
  const accessState = document.getElementById("managed-access-state");
  const accessDescription = document.getElementById("managed-access-description");
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
        btnAccessToggle.textContent = "Révoquer l'accès";
        btnAccessToggle.className = "btn btn-danger btn-sm";
      } else {
        btnAccessToggle.textContent = "Autoriser la télé-assistance";
        btnAccessToggle.className = "btn btn-primary btn-sm";
      }
    }
    consentCard?.classList.toggle("enabled", activeTicket.remote_access_enabled);
    if (accessState) accessState.textContent = activeTicket.remote_access_enabled ? "ACTIVÉE" : "DÉSACTIVÉE";
    if (accessDescription) {
      accessDescription.textContent = activeTicket.remote_access_enabled
        ? "Le technicien peut actuellement ouvrir une session à distance."
        : "Le technicien n'a pas accès au terminal de votre ordinateur.";
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
      const bubbleClass = isMe ? "message message--mine" : "message message--remote";
      const senderLabel = isMe ? "Vous" : "Technicien Opérateur";
      return `
        <div class="${bubbleClass}">
          <div class="message__meta">
            <span class="message__sender">${escapeHtml(senderLabel)}</span>
            <span class="message__time">${escapeHtml(formatTimestamp(msg.created_at))}</span>
          </div>
          <div class="message__body">${escapeHtml(msg.body)}</div>
          ${isMe ? `<span class="message__state ${msg.delivery_state === "FAILED" ? "message__state--failed" : ""}">${escapeHtml(msg.delivery_state === "DELIVERED" ? "Livré" : msg.delivery_state === "FAILED" ? "Échec — réessayez" : "Envoi…")}</span>` : ""}
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
          <div class="attachment-actions">
            <button class="btn-icon attachment-action" data-attachment-action="open" data-path="${escapeHtml(att.local_path)}" title="Ouvrir" aria-label="Ouvrir ${escapeHtml(att.filename)}"><i class="ph ph-arrow-square-out"></i></button>
            <button class="btn-icon attachment-action" data-attachment-action="reveal" data-path="${escapeHtml(att.local_path)}" title="Afficher dans le dossier" aria-label="Afficher ${escapeHtml(att.filename)} dans le dossier"><i class="ph ph-folder-open"></i></button>
            <button class="btn-icon attachment-action" data-attachment-action="hash" data-hash="${escapeHtml(att.sha256)}" title="Copier SHA-256" aria-label="Copier le SHA-256"><i class="ph ph-copy"></i></button>
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
  if (!container) return;

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

// ----------------------------------------------------------------------------
// Polling / State Refresh
// ----------------------------------------------------------------------------

async function refresh() {
  try {
    const status = await invoke<DesktopStatus>("desktop_status");
    const isOnline = status.agentState === "online";
    currentPeerId = status.peerId;
    updateStatusBadge(isOnline, isOnline ? "En Ligne (P2P)" : "Service Hors-Ligne");

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

        try {
          const peers = await invoke<DesktopPeer[]>("list_peers");
          renderNetworkPeers(peers, true);
        } catch (err) {
          console.warn("Failed to fetch network peers:", err);
          renderNetworkPeers([], true);
        }
      }
    } else {
      applyMode(status.mode === "managed" ? "managed" : "operator", false);
      renderTicketList([], false);
      renderManagedTicketPortal(null, false);
      renderNetworkPeers([], false);
    }
  } catch (err) {
    console.warn("Daemon unreachable:", err);
    updateStatusBadge(false, "Service Hors-Ligne");
    applyMode("operator", false);
    renderTicketList([], false);
    renderManagedTicketPortal(null, false);
    renderNetworkPeers([], false);
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
    const termDot = document.getElementById("terminal-dot");
    const termTitle = document.getElementById("terminal-title-text");
    const btnToggle = document.getElementById("btn-terminal-toggle") as HTMLButtonElement | null;
    if (termDot) termDot.className = "status-dot";
    if (termTitle) termTitle.textContent = "Terminal P2P — Session terminée";
    if (btnToggle) {
      btnToggle.disabled = false;
      btnToggle.textContent = "Démarrer Terminal";
    }
    if (term) {
      term.write("\r\n\x1b[33m[FORTIQ] Session terminal fermée.\x1b[0m\r\n");
    }
    window.setTimeout(() => setTerminalPanelVisible(false), 500);
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
  const termDot = document.getElementById("terminal-dot");
  const termTitle = document.getElementById("terminal-title-text");
  const deniedBanner = document.getElementById("terminal-denied-banner");
  const deniedMsg = document.getElementById("terminal-denied-msg");

  if (deniedBanner) deniedBanner.style.display = "none";
  setTerminalPanelVisible(true);
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
    if (termDot) termDot.className = "status-dot online";
    if (termTitle) termTitle.textContent = `Terminal actif — ${peerId.substring(0, 14)}`;
    if (btnToggle) btnToggle.textContent = "Terminer la session";
  } catch (err: any) {
    if (generation !== terminalSwitchGeneration) return;
    isTerminalActive = false;
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
    showToast("Session impossible", errMsg, "error");
  } finally {
    if (btnToggle) {
      btnToggle.disabled = false;
      if (!isTerminalActive) {
        btnToggle.textContent = "Démarrer Terminal";
      }
    }
  }
}

// ----------------------------------------------------------------------------
// Event Listeners Initialization
// ----------------------------------------------------------------------------

function initEventListeners() {
  // Navigation tabs (Tickets vs Peers)
  document.querySelectorAll<HTMLElement>(".nav-item").forEach((btn) => {
    btn.addEventListener("click", () => {
      const tab = btn.dataset.tab;
      if (tab === "tickets" || tab === "peers") {
        setOperatorTab(tab);
      }
    });
  });

  // Ticket sub-tabs (overview, chat, files, events)
  document.querySelectorAll<HTMLElement>(".ticket-tab-btn[data-ttab]").forEach((btn) => {
    btn.addEventListener("click", () => {
      const ttab = btn.dataset.ttab as "overview" | "chat" | "files" | "events";
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

  const ticketSearchInput = document.getElementById("ticket-search") as HTMLInputElement | null;
  ticketSearchInput?.addEventListener("input", () => {
    ticketSearch = ticketSearchInput.value;
    renderTicketList(ticketsCache, true);
  });
  const ticketSortSelect = document.getElementById("ticket-sort") as HTMLSelectElement | null;
  ticketSortSelect?.addEventListener("change", () => {
    ticketSort = ticketSortSelect.value as typeof ticketSort;
    renderTicketList(ticketsCache, true);
  });

  // Overview action buttons
  const btnTake = document.getElementById("btn-ticket-take");
  if (btnTake) {
    btnTake.addEventListener("click", async () => {
      if (!selectedTicketId) return;
      try {
        await invoke("update_ticket_status", { ticketId: selectedTicketId, state: "IN_PROGRESS" });
        await refresh();
        showToast("Ticket pris en charge", "Le client voit maintenant le ticket en cours.");
      } catch (err) {
        showToast("Mise à jour impossible", String(err), "error");
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
        showToast("Ticket résolu", "La résolution a été enregistrée sur le poste client.");
      } catch (err) {
        showToast("Mise à jour impossible", String(err), "error");
      }
    });
  }

  const btnClose = document.getElementById("btn-close-ticket");
  if (btnClose) {
    btnClose.addEventListener("click", async () => {
      if (!selectedTicketId) return;
      if (!await requestConfirmation(
        "Clôturer le ticket ?",
        "Le chat, les fichiers et le terminal ne seront plus disponibles.",
        "Clôturer",
      )) return;
      try {
        await invoke("update_ticket_status", { ticketId: selectedTicketId, state: "CLOSED" });
        await refresh();
        showToast("Ticket clôturé", "La demande est désormais en lecture seule.");
      } catch (err) {
        showToast("Clôture impossible", String(err), "error");
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
    const sendButton = btnOpChatSend as HTMLButtonElement | null;
    try {
      opChatInput.disabled = true;
      if (sendButton) sendButton.disabled = true;
      await invoke("send_chat_message", { ticketId: selectedTicketId, body });
      opChatInput.value = "";
      await loadSelectedTicketDetail(selectedTicketId);
    } catch (err) {
      showToast("Message non envoyé", `${String(err)} Votre texte a été conservé.`, "error");
    } finally {
      opChatInput.disabled = false;
      if (sendButton) sendButton.disabled = false;
      opChatInput.focus();
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
  const sendOpFile = async () => {
    if (!selectedTicketId) return;
    try {
      (btnOpFileSend as HTMLButtonElement | null)?.setAttribute("disabled", "true");
      const sent = await chooseAndSendFile(selectedTicketId);
      if (!sent) return;
      await loadSelectedTicketDetail(selectedTicketId);
      showToast("Fichier ajouté", "Le transfert est enregistré dans le ticket.");
    } catch (err) {
      showToast("Fichier non envoyé", String(err), "error");
    } finally {
      (btnOpFileSend as HTMLButtonElement | null)?.removeAttribute("disabled");
    }
  };
  if (btnOpFileSend) btnOpFileSend.addEventListener("click", sendOpFile);

  // Terminal Toggle Button
  const btnTermToggle = document.getElementById("btn-terminal-toggle");
  if (btnTermToggle) {
    btnTermToggle.addEventListener("click", async () => {
      if (!currentTicketDetail) return;
      const ticket = currentTicketDetail.ticket;
      if (isTerminalActive) {
        btnTermToggle.setAttribute("disabled", "true");
        btnTermToggle.textContent = "Fermeture…";
        try {
          await invoke("close_terminal_session");
          showToast("Session terminée", "Le terminal distant a été fermé.");
        } catch (err) {
          btnTermToggle.removeAttribute("disabled");
          btnTermToggle.textContent = "Terminer la session";
          showToast("Fermeture impossible", String(err), "error");
        }
        return;
      }
      queueTerminalSession(ticket.client_peer_id, ticket.id);
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
  const panelTerminal = document.querySelector(".panel-terminal");
  if (btnTermFullscreen && panelTerminal) {
    btnTermFullscreen.addEventListener("click", () => {
      panelTerminal.classList.toggle("fullscreen");
      setTimeout(() => {
        if (fitAddon) fitAddon.fit();
      }, 100);
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
        showToast("Objet requis", "Indiquez brièvement le problème rencontré.", "error");
        managedTitleInput.focus();
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
        showToast("Demande créée", "Votre technicien peut maintenant voir ce ticket.");
      } catch (err) {
        showToast("Création impossible", String(err), "error");
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
      if (!await requestConfirmation(
        "Clôturer votre demande ?",
        "La discussion, les fichiers et la télé-assistance ne seront plus disponibles.",
        "Clôturer",
      )) return;
      try {
        await invoke("update_ticket_status", {
          ticketId: managedActiveTicket.id,
          state: "CLOSED",
        });
        await refresh();
        showToast("Demande clôturée", "Le ticket est maintenant en lecture seule.");
      } catch (err) {
        showToast("Clôture impossible", String(err), "error");
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
        showToast(
          newEnabled ? "Télé-assistance autorisée" : "Télé-assistance révoquée",
          newEnabled ? "Le technicien peut maintenant ouvrir une session distante." : "Toute session active sera terminée.",
        );
      } catch (err) {
        showToast("Modification impossible", String(err), "error");
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
      clientChatInput.disabled = true;
      (btnClientChatSend as HTMLButtonElement | null)?.setAttribute("disabled", "true");
      await invoke("send_chat_message", { ticketId: managedActiveTicket.id, body });
      clientChatInput.value = "";
      await loadManagedTicketSubData(managedActiveTicket.id);
    } catch (err) {
      showToast("Message non envoyé", `${String(err)} Votre texte a été conservé.`, "error");
    } finally {
      clientChatInput.disabled = false;
      (btnClientChatSend as HTMLButtonElement | null)?.removeAttribute("disabled");
      clientChatInput.focus();
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
  const sendClientFile = async () => {
    if (!managedActiveTicket) return;
    try {
      (btnClientFileSend as HTMLButtonElement | null)?.setAttribute("disabled", "true");
      const sent = await chooseAndSendFile(managedActiveTicket.id);
      if (!sent) return;
      await loadManagedTicketSubData(managedActiveTicket.id);
      showToast("Fichier ajouté", "Votre technicien peut maintenant le consulter.");
    } catch (err) {
      showToast("Fichier non envoyé", String(err), "error");
    } finally {
      (btnClientFileSend as HTMLButtonElement | null)?.removeAttribute("disabled");
    }
  };
  if (btnClientFileSend) btnClientFileSend.addEventListener("click", sendClientFile);

  document.addEventListener("click", async (event) => {
    const button = (event.target as HTMLElement).closest<HTMLButtonElement>("[data-attachment-action]");
    if (!button) return;
    try {
      if (button.dataset.attachmentAction === "open" && button.dataset.path) {
        await openPath(button.dataset.path);
      } else if (button.dataset.attachmentAction === "reveal" && button.dataset.path) {
        await revealItemInDir(button.dataset.path);
      } else if (button.dataset.attachmentAction === "hash" && button.dataset.hash) {
        await navigator.clipboard.writeText(button.dataset.hash);
        showToast("SHA-256 copié", "L'empreinte du fichier est dans le presse-papiers.");
      }
    } catch (err) {
      showToast("Action impossible", String(err), "error");
    }
  });
}

window.addEventListener("DOMContentLoaded", () => {
  initTerminal();
  initEventListeners();
  refresh();
  setInterval(refresh, 15000);
});
