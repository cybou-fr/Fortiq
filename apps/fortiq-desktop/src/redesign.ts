interface DesktopPeer {
  peerId: string;
  hostname: string;
  os: string;
  transport: string;
  status: string;
}

interface TicketRecord {
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

let uxTickets: TicketRecord[] = [];
let uxPeers: DesktopPeer[] = [];
let searchValue = "";

const stateLabel: Record<TicketRecord["state"], string> = {
  OPEN: "Ouvert",
  IN_PROGRESS: "En cours",
  RESOLVED: "Résolu",
  CLOSED: "Fermé",
};

const priorityLabel: Record<TicketRecord["priority"], string> = {
  NORMAL: "Normal",
  HIGH: "Élevé",
  URGENT: "Urgent",
};

function el<T extends HTMLElement = HTMLElement>(id: string): T | null {
  return document.getElementById(id) as T | null;
}

function escapeHtml(text: string): string {
  const div = document.createElement("div");
  div.textContent = text;
  return div.innerHTML;
}

function formatClock(epochSecs: number): string {
  if (!epochSecs) return "—";
  return new Date(epochSecs * 1000).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
}

function formatDateTime(epochSecs: number): string {
  if (!epochSecs) return "—";
  return new Date(epochSecs * 1000).toLocaleString();
}

function shortIdentity(peerId: string): string {
  if (!peerId) return "Client";
  return `${peerId.slice(0, 8)}…`;
}

function initials(value: string): string {
  const cleaned = value.replace(/[^A-Za-z0-9 ]/g, " ").trim();
  const parts = cleaned.split(/\s+/).filter(Boolean);
  if (parts.length >= 2) return `${parts[0][0]}${parts[1][0]}`.toUpperCase();
  return (cleaned.slice(0, 2) || "PC").toUpperCase();
}

function selectedTicketId(): string | null {
  const selected = document.querySelector<HTMLElement>(".ticket-card.active");
  return selected?.dataset.ticketId || null;
}

function currentPeerForTicket(ticket: TicketRecord | undefined): DesktopPeer | undefined {
  if (!ticket) return undefined;
  return uxPeers.find((peer) => peer.peerId === ticket.client_peer_id);
}

function applyTicketSearch(): void {
  const query = searchValue.trim().toLocaleLowerCase();
  document.querySelectorAll<HTMLElement>(".ticket-card").forEach((card) => {
    if (!query) {
      card.style.display = "";
      return;
    }
    const id = card.dataset.ticketId;
    const ticket = uxTickets.find((item) => item.id === id);
    const peer = currentPeerForTicket(ticket);
    const haystack = [
      ticket?.id,
      ticket?.title,
      ticket?.description,
      ticket?.client_peer_id,
      peer?.hostname,
      peer?.os,
    ]
      .filter(Boolean)
      .join(" ")
      .toLocaleLowerCase();
    card.style.display = haystack.includes(query) ? "" : "none";
  });
}

function hydrateTicketCards(): void {
  document.querySelectorAll<HTMLElement>(".ticket-card").forEach((card) => {
    const ticket = uxTickets.find((item) => item.id === card.dataset.ticketId);
    if (!ticket) return;

    const peer = currentPeerForTicket(ticket);
    const client = peer?.hostname || `Client ${shortIdentity(ticket.client_peer_id)}`;
    const metaType = peer?.os || "Poste client";
    const preview = ticket.description?.trim() || "Aucune description fournie.";
    const access = ticket.remote_access_enabled ? "Accès distant autorisé" : "Accès distant désactivé";
    const prioClass = ticket.priority.toLowerCase();
    const renderKey = `${ticket.updated_at}:${peer?.hostname || ""}:${peer?.status || ""}`;

    if (card.dataset.uxRenderKey === renderKey) return;
    card.dataset.uxRenderKey = renderKey;

    card.innerHTML = `
      <div class="ux-ticket-row">
        <div class="ux-ticket-avatar">${escapeHtml(initials(client))}</div>
        <div class="ux-ticket-main">
          <div class="ux-ticket-client">${escapeHtml(client)}</div>
          <div class="ux-ticket-title">${escapeHtml(ticket.title)}</div>
          <div class="ux-ticket-preview">${escapeHtml(preview)}</div>
        </div>
        <time class="ux-ticket-time">${escapeHtml(formatClock(ticket.updated_at || ticket.created_at))}</time>
        <div class="ux-ticket-meta">
          <span><i class="prio-dot ${prioClass}"></i> ${escapeHtml(priorityLabel[ticket.priority])}</span>
          <span>#${escapeHtml(ticket.id.slice(0, 12))}</span>
          <span><i class="ph ph-desktop"></i> ${escapeHtml(metaType)}</span>
          <span>${escapeHtml(access)}</span>
        </div>
      </div>
    `;
  });

  applyTicketSearch();
}

function syncSelectedTicket(): void {
  const ticketId = selectedTicketId();
  if (!ticketId) {
    updateSessionBar(undefined);
    updateContext(undefined, undefined);
    return;
  }

  const ticket = uxTickets.find((item) => item.id === ticketId);
  const peer = currentPeerForTicket(ticket);
  updateSessionBar(ticket);
  updateContext(ticket, peer);

}

function updateContext(ticket: TicketRecord | undefined, peer: DesktopPeer | undefined): void {
  const state = el("ux-detail-state");
  const access = el("ux-detail-access");
  const updated = el("ux-updated-at");
  const hostname = el("ux-context-hostname");
  const os = el("ux-context-os");
  const transport = el("ux-context-transport");
  const status = el("ux-context-status");

  if (!ticket) {
    if (state) state.textContent = "—";
    if (access) access.textContent = "—";
    if (updated) updated.textContent = "—";
    if (hostname) hostname.textContent = "—";
    if (os) os.textContent = "—";
    if (transport) transport.textContent = "—";
    if (status) status.textContent = "—";
    return;
  }

  if (state) state.textContent = stateLabel[ticket.state];
  if (access) access.textContent = ticket.remote_access_enabled ? "Autorisée" : "Désactivée";
  if (updated) updated.textContent = formatDateTime(ticket.updated_at);
  if (hostname) hostname.textContent = peer?.hostname || shortIdentity(ticket.client_peer_id);
  if (os) os.textContent = peer?.os || "Inconnu";
  if (transport) transport.textContent = peer?.transport || "P2P";
  if (status) status.textContent = peer?.status || "Non détecté";
}

function updateSessionBar(ticket: TicketRecord | undefined): void {
  const state = el("ux-session-state");
  const copy = el("ux-session-copy");
  const button = el<HTMLButtonElement>("ux-open-session");
  const terminalButton = el<HTMLButtonElement>("btn-terminal-toggle");

  if (!state || !copy || !button) return;

  if (!ticket) {
    state.textContent = "Session sécurisée P2P";
    copy.textContent = "Sélectionnez un ticket pour vérifier l'autorisation de télé-assistance.";
    button.disabled = true;
    button.innerHTML = `<i class="ph ph-desktop"></i> Ouvrir une session sécurisée`;
    return;
  }

  const active = terminalButton?.textContent?.toLocaleLowerCase().includes("fermer") ?? false;
  const workState = ticket.state === "OPEN" || ticket.state === "IN_PROGRESS";

  if (active) {
    state.textContent = "Session P2P active";
    copy.textContent = "Le shell est lié exclusivement à ce ticket.";
    button.disabled = false;
    button.innerHTML = `<i class="ph ph-stop-circle"></i> Terminer la session`;
    return;
  }

  state.textContent = "Session sécurisée P2P";

  if (!workState) {
    copy.textContent = "Le ticket doit être ouvert ou en cours pour démarrer une session.";
    button.disabled = true;
  } else if (!ticket.remote_access_enabled) {
    copy.textContent = "Le client doit autoriser la télé-assistance avant l'ouverture du terminal.";
    button.disabled = true;
  } else {
    copy.textContent = "Télé-assistance autorisée par le client. La session restera liée à ce ticket.";
    button.disabled = false;
  }

  button.innerHTML = `<i class="ph ph-desktop"></i> Ouvrir une session sécurisée`;
}

function updateClock(): void {
  const now = new Date();
  const date = el("ux-topbar-date");
  const time = el("ux-topbar-time");
  if (date) {
    date.textContent = now.toLocaleDateString("fr-FR", {
      weekday: "short",
      day: "2-digit",
      month: "long",
      year: "numeric",
    });
  }
  if (time) time.textContent = now.toLocaleTimeString("fr-FR", { hour: "2-digit", minute: "2-digit" });
}

function applyOperatorSnapshot(detail: { tickets: TicketRecord[]; peers: DesktopPeer[]; peerId: string }): void {
  uxTickets = detail.tickets;
  uxPeers = detail.peers;
  const localPeer = el("ux-local-peer");
  if (localPeer) localPeer.textContent = detail.peerId ? `${detail.peerId.slice(0, 12)}…` : "Identité FORTIQ";
  hydrateTicketCards();
  syncSelectedTicket();
}

function normalizeManagedConsentLabel(): void {
  const button = el<HTMLButtonElement>("btn-managed-access-toggle");
  if (!button) return;
  const text = button.textContent || "";
  if (text.includes("AUTORISÉ")) {
    button.textContent = "Révoquer l'accès";
    button.className = "btn btn-danger btn-sm";
  } else if (text.includes("RÉVOQUÉ")) {
    button.textContent = "Autoriser la télé-assistance";
    button.className = "btn btn-primary btn-sm";
  }
}

function initSearch(): void {
  const input = el<HTMLInputElement>("ticket-search-input");
  if (!input) return;
  input.addEventListener("input", () => {
    searchValue = input.value;
    applyTicketSearch();
  });
}

function initSessionProxy(): void {
  const button = el<HTMLButtonElement>("ux-open-session");
  if (!button) return;

  button.addEventListener("click", () => {
    const terminalTab = document.querySelector<HTMLButtonElement>('.ticket-tab-btn[data-ttab="terminal"]');
    const terminalButton = el<HTMLButtonElement>("btn-terminal-toggle");
    if (!terminalButton) return;

    terminalTab?.click();
    window.setTimeout(() => {
      if (!terminalButton.disabled) terminalButton.click();
    }, 40);
  });
}

function initAccessibility(): void {
  el("btn-operator-chat-send")?.setAttribute("aria-label", "Envoyer le message");
  el("btn-client-chat-send")?.setAttribute("aria-label", "Envoyer le message");
  el("btn-term-clear")?.setAttribute("aria-label", "Effacer le terminal");
  el("btn-term-fullscreen")?.setAttribute("aria-label", "Afficher le terminal en plein écran");
}

function initMutationObserver(): void {
  const list = el("operator-ticket-list");
  if (!list) return;
  let scheduled = false;

  const observer = new MutationObserver(() => {
    if (scheduled) return;
    scheduled = true;
    window.requestAnimationFrame(() => {
      scheduled = false;
      hydrateTicketCards();
      syncSelectedTicket();
    });
  });

  observer.observe(list, { childList: true });
}

window.addEventListener("DOMContentLoaded", () => {
  updateClock();
  initSearch();
  initSessionProxy();
  initAccessibility();
  initMutationObserver();

  window.addEventListener("fortiq:operator-snapshot", (event) => {
    applyOperatorSnapshot((event as CustomEvent<{ tickets: TicketRecord[]; peers: DesktopPeer[]; peerId: string }>).detail);
  });

  window.setInterval(updateClock, 30_000);
  window.setInterval(() => {
    hydrateTicketCards();
    syncSelectedTicket();
    normalizeManagedConsentLabel();
  }, 700);
});
