import { invoke } from "@tauri-apps/api/core";

interface DesktopStatus {
  product: string;
  version: string;
  agentState: string;
  mode: string;
  peerId: string;
  activeTicketId?: string | null;
  activeTicketState?: string | null;
  authorizedOperator?: string | null;
}

interface DesktopPeer {
  peerId: string;
  hostname: string;
  os: string;
  transport: string;
  status: string;
}

let selectedPeerId: string | null = null;

function escapeHtml(text: string): string {
  const div = document.createElement("div");
  div.textContent = text;
  return div.innerHTML;
}

function applyMode(mode: "operator" | "managed", isOnline: boolean) {
  const operatorView = document.getElementById("operator-view");
  const managedView = document.getElementById("managed-view");
  const brandModeEl = document.getElementById("brand-mode");
  const userRoleEl = document.getElementById("user-role");

  if (mode === "operator") {
    if (operatorView) operatorView.style.display = "grid";
    if (managedView) managedView.style.display = "none";
    if (brandModeEl) brandModeEl.textContent = "CONSOLE OPÉRATEUR";
    if (userRoleEl) userRoleEl.textContent = isOnline ? "OPÉRATEUR" : "DÉCONNECTÉ";
  } else {
    if (operatorView) operatorView.style.display = "none";
    if (managedView) managedView.style.display = "flex";
    if (brandModeEl) brandModeEl.textContent = "CLIENT MANAGÉ";
    if (userRoleEl) userRoleEl.textContent = isOnline ? "CLIENT MANAGÉ" : "DÉCONNECTÉ";
  }
}

function updateStatusBadge(isOnline: boolean, stateText: string) {
  const statusDot = document.getElementById("status-dot");
  const agentStateEl = document.getElementById("agent-state");
  const managedDot = document.getElementById("managed-beacon-dot");
  const managedBeaconText = document.getElementById("managed-beacon-text");
  const opOfflineAlert = document.getElementById("operator-offline-alert");
  const managedOfflineAlert = document.getElementById("managed-offline-alert");

  if (isOnline) {
    if (statusDot) {
      statusDot.className = "status-dot online";
    }
    if (agentStateEl) {
      agentStateEl.textContent = stateText;
      agentStateEl.className = "user-state";
    }
    if (managedDot) {
      managedDot.className = "status-dot online";
    }
    if (managedBeaconText) {
      managedBeaconText.textContent = "Service Agent Actif · Réseau P2P Sécurisé";
      managedBeaconText.style.color = "#7ee787";
    }
    if (opOfflineAlert) opOfflineAlert.style.display = "none";
    if (managedOfflineAlert) managedOfflineAlert.style.display = "none";
  } else {
    if (statusDot) {
      statusDot.className = "status-dot offline";
    }
    if (agentStateEl) {
      agentStateEl.textContent = "Service Hors-Ligne";
      agentStateEl.className = "user-state offline";
    }
    if (managedDot) {
      managedDot.className = "status-dot offline";
    }
    if (managedBeaconText) {
      managedBeaconText.textContent = "Service Démon Indisponible";
      managedBeaconText.style.color = "var(--accent-red)";
    }
    if (opOfflineAlert) opOfflineAlert.style.display = "flex";
    if (managedOfflineAlert) managedOfflineAlert.style.display = "flex";
  }
}

function updateManagedTicketUI(
  ticketId: string | null,
  operatorId?: string | null,
  isOnline: boolean = true
) {
  const managedClosed = document.getElementById("managed-state-closed");
  const managedOpen = document.getElementById("managed-state-open");
  const managedTicketIdEl = document.getElementById("managed-ticket-id");
  const managedOperatorIdEl = document.getElementById("managed-operator-id");
  const btnManagedOpen = document.getElementById("btn-managed-open") as HTMLButtonElement | null;

  if (btnManagedOpen) {
    btnManagedOpen.disabled = !isOnline;
  }

  if (ticketId) {
    if (managedClosed) managedClosed.style.display = "none";
    if (managedOpen) managedOpen.style.display = "flex";
    if (managedTicketIdEl) managedTicketIdEl.textContent = ticketId;
    if (managedOperatorIdEl && operatorId) {
      managedOperatorIdEl.textContent = operatorId;
    }
  } else {
    if (managedClosed) managedClosed.style.display = "flex";
    if (managedOpen) managedOpen.style.display = "none";
  }
}

function clearPeerDetails() {
  const detailTitle = document.getElementById("detail-ticket-title");
  const detailHostname = document.getElementById("detail-hostname");
  const detailPeerId = document.getElementById("detail-peer-id");
  const detailOs = document.getElementById("detail-os");
  const detailTransport = document.getElementById("detail-transport");
  const detailBadge = document.getElementById("detail-connection-badge");
  const btnClose = document.getElementById("btn-close-ticket") as HTMLButtonElement | null;
  const btnConnect = document.getElementById("btn-connect") as HTMLButtonElement | null;
  const termDot = document.getElementById("terminal-dot");
  const termTitle = document.getElementById("terminal-title-text");

  if (detailTitle) detailTitle.textContent = "Détails de Session";
  if (detailHostname) detailHostname.textContent = "—";
  if (detailPeerId) detailPeerId.textContent = "—";
  if (detailOs) detailOs.textContent = "—";
  if (detailTransport) detailTransport.textContent = "—";
  if (detailBadge) {
    detailBadge.textContent = "Non sélectionné";
    detailBadge.className = "badge";
  }
  if (btnClose) btnClose.disabled = true;
  if (btnConnect) btnConnect.disabled = true;
  if (termDot) termDot.className = "status-dot";
  if (termTitle) termTitle.textContent = "Terminal P2P";
}

function updatePeerDetails(peer: DesktopPeer) {
  const detailTitle = document.getElementById("detail-ticket-title");
  const detailHostname = document.getElementById("detail-hostname");
  const detailPeerId = document.getElementById("detail-peer-id");
  const detailOs = document.getElementById("detail-os");
  const detailTransport = document.getElementById("detail-transport");
  const detailBadge = document.getElementById("detail-connection-badge");
  const btnClose = document.getElementById("btn-close-ticket") as HTMLButtonElement | null;
  const btnConnect = document.getElementById("btn-connect") as HTMLButtonElement | null;
  const termDot = document.getElementById("terminal-dot");
  const termTitle = document.getElementById("terminal-title-text");

  const displayName = peer.hostname || peer.peerId.substring(0, 14);
  const isConnected = peer.status.toLowerCase() === "connected";

  if (detailTitle) detailTitle.textContent = `Session ${displayName}`;
  if (detailHostname) detailHostname.textContent = peer.hostname || "Inconnu";
  if (detailPeerId) detailPeerId.textContent = peer.peerId;
  if (detailOs) detailOs.textContent = peer.os || "Inconnu";
  if (detailTransport) detailTransport.textContent = peer.transport || "QUIC Direct P2P";
  if (detailBadge) {
    detailBadge.textContent = isConnected ? "Connecté & Prêt" : peer.status;
    detailBadge.className = isConnected ? "badge online" : "badge";
  }
  if (btnClose) btnClose.disabled = !isConnected;
  if (btnConnect) btnConnect.disabled = !isConnected;
  if (termDot) termDot.className = isConnected ? "status-dot online" : "status-dot";
  if (termTitle) termTitle.textContent = `Terminal — ${displayName}`;
}

function renderPeerList(peers: DesktopPeer[], isOnline: boolean) {
  const container = document.getElementById("operator-ticket-list");
  const countBadge = document.getElementById("tickets-count-badge");
  if (!container) return;

  if (countBadge) {
    countBadge.textContent = `${peers.length} Poste${peers.length > 1 ? "s" : ""}`;
    countBadge.className = peers.length > 0 ? "badge open" : "badge";
  }

  if (!isOnline || peers.length === 0) {
    container.innerHTML = `
      <div class="empty-state">
        <i class="ph ph-desktop-tower"></i>
        <div class="empty-state-title">${isOnline ? "Aucun poste détecté" : "Service déconnecté"}</div>
        <div class="empty-state-subtitle">${
          isOnline
            ? "En attente de connexion P2P ou d'enregistrement rendezvous."
            : "Démarrez fortiq-service pour vous connecter au réseau souverain."
        }</div>
      </div>
    `;
    clearPeerDetails();
    return;
  }

  container.innerHTML = "";

  if (!selectedPeerId || !peers.some((p) => p.peerId === selectedPeerId)) {
    selectedPeerId = peers[0].peerId;
  }

  for (const peer of peers) {
    const card = document.createElement("div");
    const isSelected = peer.peerId === selectedPeerId;
    card.className = `ticket-card ${isSelected ? "active" : ""}`;
    card.dataset.peerId = peer.peerId;

    const isConnected = peer.status.toLowerCase() === "connected";
    const statusText = isConnected ? "CONNECTÉ" : peer.status.toUpperCase();
    const displayName = peer.hostname || `${peer.peerId.substring(0, 14)}...`;

    card.innerHTML = `
      <div class="ticket-card-header">
        <span class="ticket-badge ${isConnected ? "open" : ""}">${escapeHtml(statusText)}</span>
        <span class="ticket-id">${escapeHtml(displayName)}</span>
      </div>
      <div class="ticket-card-title">${escapeHtml(peer.os || "Système distant")}</div>
      <div class="ticket-card-meta">
        <span>${escapeHtml(peer.transport || "QUIC Direct")}</span>
        <span>•</span>
        <span class="code" title="${escapeHtml(peer.peerId)}">${escapeHtml(peer.peerId.substring(0, 10))}...</span>
      </div>
    `;

    card.addEventListener("click", () => {
      selectedPeerId = peer.peerId;
      document.querySelectorAll(".ticket-card").forEach((c) => c.classList.remove("active"));
      card.classList.add("active");
      updatePeerDetails(peer);
    });

    container.appendChild(card);
  }

  const activePeer = peers.find((p) => p.peerId === selectedPeerId);
  if (activePeer) {
    updatePeerDetails(activePeer);
  }
}

async function refresh() {
  try {
    const status = await invoke<DesktopStatus>("desktop_status");
    const isOnline = status.agentState === "online";
    updateStatusBadge(isOnline, isOnline ? "En Ligne (P2P)" : "Service Hors-Ligne");

    if (isOnline) {
      const mode = status.mode.toLowerCase() === "managed" ? "managed" : "operator";
      applyMode(mode, true);

      if (mode === "managed") {
        updateManagedTicketUI(
          status.activeTicketId ?? null,
          status.authorizedOperator ?? null,
          true
        );
      } else {
        try {
          const peers = await invoke<DesktopPeer[]>("list_peers");
          renderPeerList(peers, true);
        } catch (err) {
          console.warn("Failed to fetch peers:", err);
          renderPeerList([], true);
        }
      }
    } else {
      applyMode(status.mode === "managed" ? "managed" : "operator", false);
      updateManagedTicketUI(null, null, false);
      renderPeerList([], false);
    }
  } catch (err) {
    console.warn("Daemon unreachable:", err);
    updateStatusBadge(false, "Service Hors-Ligne");
    applyMode("operator", false);
    updateManagedTicketUI(null, null, false);
    renderPeerList([], false);
  }
}

function initEventListeners() {
  // Open ticket in managed mode
  const btnManagedOpen = document.getElementById("btn-managed-open") as HTMLButtonElement | null;
  if (btnManagedOpen) {
    btnManagedOpen.addEventListener("click", async () => {
      try {
        btnManagedOpen.disabled = true;
        btnManagedOpen.innerHTML = `<span>Ouverture en cours...</span>`;
        const ticketId = await invoke<string>("open_ticket");
        updateManagedTicketUI(ticketId, null, true);
      } catch (err) {
        console.error("Failed to open ticket:", err);
        alert(`Échec de l'ouverture du ticket : ${err}`);
      } finally {
        btnManagedOpen.disabled = false;
        btnManagedOpen.innerHTML = `<i class="ph ph-ticket"></i><span>Ouvrir un Ticket de Support</span>`;
        refresh();
      }
    });
  }

  // Close ticket in operator mode
  const btnCloseTicket = document.getElementById("btn-close-ticket") as HTMLButtonElement | null;
  if (btnCloseTicket) {
    btnCloseTicket.addEventListener("click", async () => {
      if (!selectedPeerId) return;
      try {
        btnCloseTicket.disabled = true;
        btnCloseTicket.textContent = "Clôture en cours...";
        await invoke("close_ticket", { peer: selectedPeerId });

        const terminalView = document.getElementById("terminal-view");
        if (terminalView) {
          const line = document.createElement("div");
          line.className = "terminal-line banner";
          line.textContent = `[${new Date().toLocaleTimeString()}] Ticket clôturé avec succès pour le pair ${selectedPeerId}.`;
          terminalView.appendChild(line);
          terminalView.scrollTop = terminalView.scrollHeight;
        }
      } catch (err) {
        console.error("Failed to close ticket:", err);
        alert(`Échec de la clôture du ticket : ${err}`);
      } finally {
        btnCloseTicket.disabled = false;
        btnCloseTicket.textContent = "Clôturer Ticket";
        refresh();
      }
    });
  }

  // Connect terminal action
  const btnConnect = document.getElementById("btn-connect");
  const terminalView = document.getElementById("terminal-view");
  if (btnConnect && terminalView) {
    btnConnect.addEventListener("click", () => {
      if (!selectedPeerId) return;
      const line = document.createElement("div");
      line.className = "terminal-line banner";
      line.textContent = `[${new Date().toLocaleTimeString()}] Session P2P authentifiée avec ${selectedPeerId}. Flux ConPTY/xterm prévu pour le Milestone 12.`;
      terminalView.appendChild(line);
      terminalView.scrollTop = terminalView.scrollHeight;
    });
  }

  // Clear terminal action
  const btnTermClear = document.getElementById("btn-term-clear");
  if (btnTermClear && terminalView) {
    btnTermClear.addEventListener("click", () => {
      terminalView.innerHTML = `
        <div class="terminal-line banner">Terminal P2P — En attente de session (M12 ConPTY/xterm)</div>
        <div class="terminal-line prompt">FORTIQ&gt; <span class="cursor">_</span></div>
      `;
    });
  }

  // Handle nav tab switching
  const navItems = document.querySelectorAll(".nav-item");
  navItems.forEach((btn) => {
    btn.addEventListener("click", () => {
      navItems.forEach((item) => item.classList.remove("active"));
      btn.classList.add("active");
    });
  });
}

window.addEventListener("DOMContentLoaded", () => {
  initEventListeners();
  refresh();
  // Poll daemon state every 3 seconds
  setInterval(refresh, 3000);
});
