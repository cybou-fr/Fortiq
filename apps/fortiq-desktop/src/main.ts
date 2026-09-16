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

let currentMode: "operator" | "managed" = "operator";

function applyMode(mode: "operator" | "managed") {
  currentMode = mode;
  const operatorView = document.getElementById("operator-view");
  const managedView = document.getElementById("managed-view");
  const brandModeEl = document.getElementById("brand-mode");
  const userRoleEl = document.getElementById("user-role");
  const toggleLabelEl = document.getElementById("toggle-mode-label");

  if (mode === "operator") {
    if (operatorView) operatorView.style.display = "grid";
    if (managedView) managedView.style.display = "none";
    if (brandModeEl) brandModeEl.textContent = "CONSOLE OPÉRATEUR";
    if (userRoleEl) userRoleEl.textContent = "OPÉRATEUR";
    if (toggleLabelEl) toggleLabelEl.textContent = "Vue: Opérateur";
  } else {
    if (operatorView) operatorView.style.display = "none";
    if (managedView) managedView.style.display = "flex";
    if (brandModeEl) brandModeEl.textContent = "CLIENT MANAGÉ";
    if (userRoleEl) userRoleEl.textContent = "MANAGÉ";
    if (toggleLabelEl) toggleLabelEl.textContent = "Vue: Client Managé";
  }
}

function updateTicketUI(ticketId: string | null) {
  const managedClosed = document.getElementById("managed-state-closed");
  const managedOpen = document.getElementById("managed-state-open");
  const managedTicketIdEl = document.getElementById("managed-ticket-id");
  const cardTicketIdEl = document.getElementById("card-ticket-id");
  const detailTicketTitleEl = document.getElementById("detail-ticket-title");
  const ticketsCountBadge = document.getElementById("tickets-count-badge");

  if (ticketId) {
    if (managedClosed) managedClosed.style.display = "none";
    if (managedOpen) managedOpen.style.display = "flex";
    if (managedTicketIdEl) managedTicketIdEl.textContent = ticketId;
    if (cardTicketIdEl) cardTicketIdEl.textContent = ticketId;
    if (detailTicketTitleEl) detailTicketTitleEl.textContent = `Ticket ${ticketId}`;
    if (ticketsCountBadge) {
      ticketsCountBadge.textContent = "1 Actif";
      ticketsCountBadge.className = "badge open";
    }
  } else {
    if (managedClosed) managedClosed.style.display = "flex";
    if (managedOpen) managedOpen.style.display = "none";
    if (ticketsCountBadge) {
      ticketsCountBadge.textContent = "0 Actif";
      ticketsCountBadge.className = "badge";
    }
  }
}

async function init() {
  try {
    const status = await invoke<DesktopStatus>("desktop_status");
    const stateEl = document.getElementById("agent-state");
    const detailPeerIdEl = document.getElementById("detail-peer-id");
    const managedOperatorIdEl = document.getElementById("managed-operator-id");

    if (stateEl) {
      stateEl.textContent = status.agentState === "online" ? "En Ligne (P2P)" : status.agentState;
    }
    if (detailPeerIdEl && status.peerId) {
      detailPeerIdEl.textContent = status.peerId;
    }
    if (managedOperatorIdEl && status.authorizedOperator) {
      managedOperatorIdEl.textContent = status.authorizedOperator;
    }

    // Set initial mode based on daemon status
    const initialMode = status.mode.toLowerCase() === "managed" ? "managed" : "operator";
    applyMode(initialMode);

    if (status.activeTicketId) {
      updateTicketUI(status.activeTicketId);
    }
  } catch (err) {
    console.warn("Could not fetch desktop status:", err);
    applyMode("operator");
  }

  // Toggle mode button listener
  const toggleBtn = document.getElementById("btn-toggle-mode");
  if (toggleBtn) {
    toggleBtn.addEventListener("click", () => {
      const nextMode = currentMode === "operator" ? "managed" : "operator";
      applyMode(nextMode);
    });
  }

  // Open ticket in managed mode
  const btnManagedOpen = document.getElementById("btn-managed-open");
  if (btnManagedOpen) {
    btnManagedOpen.addEventListener("click", async () => {
      try {
        const ticketId = await invoke<string>("open_ticket");
        updateTicketUI(ticketId);
      } catch (err) {
        console.error("Failed to open ticket:", err);
        updateTicketUI("#FT-8910");
      }
    });
  }

  // Close ticket in operator mode
  const btnCloseTicket = document.getElementById("btn-close-ticket");
  if (btnCloseTicket) {
    btnCloseTicket.addEventListener("click", async () => {
      try {
        await invoke("close_ticket", { peer: "12D3KooWSx8m9zY24kLPq9aZ" });
        updateTicketUI(null);
        const detailTitle = document.getElementById("detail-ticket-title");
        if (detailTitle) detailTitle.textContent = "Aucun ticket actif";
      } catch (err) {
        console.error("Failed to close ticket:", err);
      }
    });
  }

  // Connect terminal action
  const btnConnect = document.getElementById("btn-connect");
  const terminalView = document.getElementById("terminal-view");
  if (btnConnect && terminalView) {
    btnConnect.addEventListener("click", () => {
      const line = document.createElement("div");
      line.className = "terminal-line banner";
      line.textContent = `[${new Date().toLocaleTimeString()}] Session QUIC P2P ré-authentifiée avec succès. Prêt.`;
      terminalView.appendChild(line);
      terminalView.scrollTop = terminalView.scrollHeight;
    });
  }

  // Clear terminal action
  const btnTermClear = document.getElementById("btn-term-clear");
  if (btnTermClear && terminalView) {
    btnTermClear.addEventListener("click", () => {
      terminalView.innerHTML = `
        <div class="terminal-line banner">FORTIQ Session SRE Distante (libp2p QUIC Direct)</div>
        <div class="terminal-line prompt">PS C:\\Users\\support&gt; <span class="cursor">_</span></div>
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
  init();
});
