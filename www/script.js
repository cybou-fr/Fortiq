// ==========================================================================
// FORTIQ Agency Landing Page Interactive Script
// ==========================================================================

document.addEventListener("DOMContentLoaded", () => {
  // Mobile Drawer Toggle
  const menuToggle = document.getElementById("menuToggle");
  const mobileDrawer = document.getElementById("mobileDrawer");

  if (menuToggle && mobileDrawer) {
    menuToggle.addEventListener("click", () => {
      mobileDrawer.classList.toggle("active");
    });

    // Close mobile menu when clicking a link
    mobileDrawer.querySelectorAll("a").forEach((link) => {
      link.addEventListener("click", () => {
        mobileDrawer.classList.remove("active");
      });
    });
  }

  // Download Modal Elements
  const modal = document.getElementById("downloadModal");
  const modalTitle = document.getElementById("modalTitle");
  const modalPackageName = document.getElementById("modalPackageName");
  const modalDesc = document.getElementById("modalDesc");
  const modalClose = document.getElementById("modalClose");
  const modalDownloadBtn = document.getElementById("modalDownloadBtn");

  const packages = {
    Windows: {
      file: "fortiq-agent-v0.1.0-x64.msi",
      title: "Download for Windows (Installer)",
      desc: "Windows 10/11 & Windows Server. Automatically configures the fortiq-service Windows service.",
    },
    "Windows Portable": {
      file: "fortiq-agent-v0.1.0-portable.exe",
      title: "Download for Windows (Portable)",
      desc: "Standalone zero-install binary for rapid on-demand remote support sessions.",
    },
    "Linux .deb": {
      file: "fortiq-agent_0.1.0_amd64.deb",
      title: "Download Debian/Ubuntu Package",
      desc: "Installs fortiq-service and systemd service unit. Supports Debian 11+, Ubuntu 20.04+.",
    },
    "Linux Tarball": {
      file: "fortiq-agent-v0.1.0-linux-x86_64.tar.gz",
      title: "Download Linux Tarball",
      desc: "Static binary with sample configuration for custom server environments and containers.",
    },
    "macOS Universal": {
      file: "fortiq-agent-v0.1.0-universal.dmg",
      title: "Download for macOS",
      desc: "Universal binary for Apple Silicon (M1/M2/M3/M4) and Intel Macs.",
    },
    Homebrew: {
      file: "brew install fortiq-agent",
      title: "Install via Homebrew",
      desc: "Run 'brew tap fortiq/agent && brew install fortiq-agent' in your terminal.",
    },
  };

  // Open modal on download button click
  document.querySelectorAll(".dl-btn").forEach((btn) => {
    btn.addEventListener("click", (e) => {
      e.preventDefault();
      const os = btn.getAttribute("data-os") || "Windows";
      const pkg = packages[os] || packages["Windows"];

      if (modal && modalTitle && modalPackageName && modalDesc) {
        modalTitle.textContent = pkg.title;
        modalPackageName.textContent = pkg.file;
        modalDesc.textContent = pkg.desc;
        modal.classList.add("active");
      }
    });
  });

  // Close modal
  const closeModal = () => {
    if (modal) modal.classList.remove("active");
  };

  if (modalClose) modalClose.addEventListener("click", closeModal);

  window.addEventListener("click", (e) => {
    if (e.target === modal) closeModal();
  });

  window.addEventListener("keydown", (e) => {
    if (e.key === "Escape") closeModal();
  });

  if (modalDownloadBtn) {
    modalDownloadBtn.addEventListener("click", () => {
      const originalText = modalDownloadBtn.textContent;
      modalDownloadBtn.textContent = "Connecting to Release CDN...";
      modalDownloadBtn.disabled = true;

      setTimeout(() => {
        modalDownloadBtn.textContent = "✓ Download Started";
        setTimeout(() => {
          modalDownloadBtn.textContent = originalText;
          modalDownloadBtn.disabled = false;
          closeModal();
        }, 1500);
      }, 1000);
    });
  }

  // Contact Form Submission Simulator
  const contactForm = document.getElementById("contactForm");
  const formSuccess = document.getElementById("formSuccess");

  if (contactForm && formSuccess) {
    contactForm.addEventListener("submit", (e) => {
      e.preventDefault();
      const submitBtn = contactForm.querySelector("button[type='submit']");
      if (submitBtn) {
        submitBtn.textContent = "Sending Request...";
        submitBtn.disabled = true;
      }

      setTimeout(() => {
        contactForm.style.display = "none";
        formSuccess.style.display = "block";
      }, 900);
    });
  }
});
