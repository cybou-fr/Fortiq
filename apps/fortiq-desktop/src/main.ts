import { invoke } from "@tauri-apps/api/core";

interface DesktopStatus {
  product: string;
  version: string;
  agentState: string;
  mode: string;
}

async function init() {
  try {
    const status = await invoke<DesktopStatus>("desktop_status");
    const roleEl = document.getElementById("user-role");
    const stateEl = document.getElementById("agent-state");
    const brandModeEl = document.getElementById("brand-mode");

    if (roleEl) {
      roleEl.textContent = status.mode.toUpperCase();
    }
    if (stateEl) {
      stateEl.textContent = status.agentState === "online" ? "Connected" : status.agentState;
    }
    if (brandModeEl) {
      brandModeEl.textContent = `${status.mode.toUpperCase()} CONSOLE`;
    }
  } catch (err) {
    console.warn("Could not fetch desktop status:", err);
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
