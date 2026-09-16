// ==========================================================================
// FORTIQ — Support & Infogérance IT Souveraine
// P2P Particle Mesh Engine, Scroll Reveal & Interactive UX
// ==========================================================================

class P2PParticleCanvas {
  constructor(canvasId = "particleCanvas") {
    this.canvas = document.getElementById(canvasId);
    if (!this.canvas) {
      this.canvas = document.createElement("canvas");
      this.canvas.id = canvasId;
      this.canvas.className = "particle-canvas";
      document.body.prepend(this.canvas);
    }
    this.ctx = this.canvas.getContext("2d");
    this.particles = [];
    this.mouse = { x: null, y: null, radius: 140, active: false };
    this.animationFrameId = null;
    this.isRunning = false;

    this.init();
  }

  init() {
    this.resize();
    this.createParticles();
    this.bindEvents();
    this.start();
  }

  resize() {
    this.dpr = Math.min(window.devicePixelRatio || 1, 2);
    this.width = window.innerWidth;
    this.height = window.innerHeight;

    this.canvas.width = this.width * this.dpr;
    this.canvas.height = this.height * this.dpr;
    this.canvas.style.width = `${this.width}px`;
    this.canvas.style.height = `${this.height}px`;

    this.ctx.scale(this.dpr, this.dpr);
  }

  createParticles() {
    this.particles = [];
    const baseCount = Math.floor((this.width * this.height) / 16000);
    const count = Math.max(35, Math.min(baseCount, 85));

    const colors = [
      { r: 0, g: 150, b: 255 },  // Vivid electric blue
      { r: 0, g: 220, b: 255 },  // Bright cyan
      { r: 99, g: 150, b: 255 }, // Indigo azure
      { r: 52, g: 211, b: 153 }  // Emerald cyber green
    ];

    for (let i = 0; i < count; i++) {
      const color = colors[Math.floor(Math.random() * (i % 7 === 0 ? colors.length : colors.length - 1))];
      this.particles.push({
        x: Math.random() * this.width,
        y: Math.random() * this.height,
        vx: (Math.random() - 0.5) * 0.6,
        vy: (Math.random() - 0.5) * 0.6,
        radius: Math.random() * 2.2 + 1.6,
        color: color,
        alpha: Math.random() * 0.35 + 0.55,
        pulseSpeed: 0.018 + Math.random() * 0.025,
        pulseVal: Math.random() * Math.PI * 2
      });
    }
  }

  bindEvents() {
    let resizeTimeout;
    window.addEventListener("resize", () => {
      clearTimeout(resizeTimeout);
      resizeTimeout = setTimeout(() => {
        this.resize();
        this.createParticles();
      }, 150);
    });

    window.addEventListener("mousemove", (e) => {
      this.mouse.x = e.clientX;
      this.mouse.y = e.clientY;
      this.mouse.active = true;
    });

    window.addEventListener("mouseleave", () => {
      this.mouse.active = false;
      this.mouse.x = null;
      this.mouse.y = null;
    });

    document.addEventListener("visibilitychange", () => {
      if (document.hidden) {
        this.stop();
      } else {
        this.start();
      }
    });
  }

  start() {
    if (!this.isRunning) {
      this.isRunning = true;
      this.loop();
    }
  }

  stop() {
    this.isRunning = false;
    if (this.animationFrameId) {
      cancelAnimationFrame(this.animationFrameId);
      this.animationFrameId = null;
    }
  }

  loop() {
    if (!this.isRunning) return;
    this.draw();
    this.animationFrameId = requestAnimationFrame(() => this.loop());
  }

  draw() {
    this.ctx.clearRect(0, 0, this.width, this.height);

    const maxConnectionDist = this.width < 768 ? 105 : 140;
    const maxConnectionDistSq = maxConnectionDist * maxConnectionDist;
    const mouseRadiusSq = this.mouse.radius * this.mouse.radius;

    for (let i = 0; i < this.particles.length; i++) {
      const p = this.particles[i];

      p.x += p.vx;
      p.y += p.vy;

      if (p.x < -10) p.x = this.width + 10;
      else if (p.x > this.width + 10) p.x = -10;
      if (p.y < -10) p.y = this.height + 10;
      else if (p.y > this.height + 10) p.y = -10;

      p.pulseVal += p.pulseSpeed;
      const currentAlpha = p.alpha + Math.sin(p.pulseVal) * 0.18;

      // Interaction with mouse cursor
      if (this.mouse.active && this.mouse.x !== null) {
        const dx = this.mouse.x - p.x;
        const dy = this.mouse.y - p.y;
        const distSq = dx * dx + dy * dy;

        if (distSq < mouseRadiusSq) {
          const dist = Math.sqrt(distSq);
          const force = (1 - dist / this.mouse.radius) * 0.03;
          p.x += dx * force;
          p.y += dy * force;

          const mouseLineAlpha = (1 - dist / this.mouse.radius) * 0.45;
          this.ctx.beginPath();
          this.ctx.moveTo(p.x, p.y);
          this.ctx.lineTo(this.mouse.x, this.mouse.y);
          this.ctx.strokeStyle = `rgba(0, 220, 255, ${mouseLineAlpha})`;
          this.ctx.lineWidth = 1.1;
          this.ctx.stroke();
        }
      }

      // Draw particle dot
      this.ctx.beginPath();
      this.ctx.arc(p.x, p.y, p.radius, 0, Math.PI * 2);
      this.ctx.fillStyle = `rgba(${p.color.r}, ${p.color.g}, ${p.color.b}, ${Math.max(0.15, currentAlpha)})`;
      this.ctx.fill();

      // Soft halo glow around node
      this.ctx.beginPath();
      this.ctx.arc(p.x, p.y, p.radius * 2.8, 0, Math.PI * 2);
      this.ctx.fillStyle = `rgba(${p.color.r}, ${p.color.g}, ${p.color.b}, ${Math.max(0.04, currentAlpha * 0.3)})`;
      this.ctx.fill();

      // Connect with neighboring particles (P2P mesh lines)
      for (let j = i + 1; j < this.particles.length; j++) {
        const p2 = this.particles[j];
        const dx = p.x - p2.x;
        const dy = p.y - p2.y;
        const distSq = dx * dx + dy * dy;

        if (distSq < maxConnectionDistSq) {
          const dist = Math.sqrt(distSq);
          const lineAlpha = (1 - dist / maxConnectionDist) * 0.28;
          this.ctx.beginPath();
          this.ctx.moveTo(p.x, p.y);
          this.ctx.lineTo(p2.x, p2.y);
          this.ctx.strokeStyle = `rgba(0, 200, 255, ${lineAlpha})`;
          this.ctx.lineWidth = 0.9;
          this.ctx.stroke();
        }
      }
    }
  }
}

// Scroll Reveal Observer
function initScrollReveal() {
  const prefersReducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const revealElements = document.querySelectorAll(".reveal");

  if (prefersReducedMotion || !("IntersectionObserver" in window)) {
    revealElements.forEach((el) => el.classList.add("revealed"));
    return;
  }

  const observer = new IntersectionObserver(
    (entries) => {
      entries.forEach((entry) => {
        if (entry.isIntersecting) {
          entry.target.classList.add("revealed");
          observer.unobserve(entry.target);
        }
      });
    },
    {
      root: null,
      threshold: 0.1,
      rootMargin: "0px 0px -40px 0px",
    }
  );

  revealElements.forEach((el) => observer.observe(el));
}

// Header Dynamic Elevation on Scroll
function initHeaderScroll() {
  const header = document.querySelector(".site-header");
  if (!header) return;

  const onScroll = () => {
    if (window.scrollY > 20) {
      header.classList.add("scrolled");
    } else {
      header.classList.remove("scrolled");
    }
  };

  window.addEventListener("scroll", onScroll, { passive: true });
  onScroll();
}

document.addEventListener("DOMContentLoaded", () => {
  // Initialize P2P Particle Mesh Network
  new P2PParticleCanvas("particleCanvas");

  // Initialize Scroll Reveal & Header Elevation
  initScrollReveal();
  initHeaderScroll();

  // Mobile menu (Drawer)
  const menuToggle = document.getElementById("menuToggle");
  const mobileDrawer = document.getElementById("mobileDrawer");

  if (menuToggle && mobileDrawer) {
    menuToggle.addEventListener("click", () => {
      mobileDrawer.classList.toggle("active");
    });

    mobileDrawer.querySelectorAll("a").forEach((link) => {
      link.addEventListener("click", () => {
        mobileDrawer.classList.remove("active");
      });
    });
  }

  // Contact / Diagnostic form handler
  const contactForm = document.getElementById("contactForm");
  const formSuccess = document.getElementById("formSuccess");

  if (contactForm && formSuccess) {
    contactForm.addEventListener("submit", (e) => {
      e.preventDefault();
      const submitBtn = contactForm.querySelector("button[type='submit']");
      if (submitBtn) {
        submitBtn.textContent = "Envoi de votre demande en cours...";
        submitBtn.disabled = true;
      }

      setTimeout(() => {
        contactForm.style.display = "none";
        formSuccess.style.display = "block";
      }, 800);
    });
  }

  // FAQ Accordion Handler (Accessible with ARIA)
  const faqButtons = document.querySelectorAll(".faq-button");
  faqButtons.forEach((btn) => {
    btn.addEventListener("click", () => {
      const item = btn.closest(".faq-item");
      if (!item) return;
      const isExpanded = btn.getAttribute("aria-expanded") === "true";
      
      btn.setAttribute("aria-expanded", String(!isExpanded));
      item.classList.toggle("active");
    });
  });
});


