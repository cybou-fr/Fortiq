// ==========================================================================
// FORTIQ — Support & Infogérance IT Souveraine
// Script interactif pour la navigation et le formulaire de contact
// ==========================================================================

document.addEventListener("DOMContentLoaded", () => {
  // Menu mobile (Drawer)
  const menuToggle = document.getElementById("menuToggle");
  const mobileDrawer = document.getElementById("mobileDrawer");

  if (menuToggle && mobileDrawer) {
    menuToggle.addEventListener("click", () => {
      mobileDrawer.classList.toggle("active");
    });

    // Fermer le menu au clic sur un lien
    mobileDrawer.querySelectorAll("a").forEach((link) => {
      link.addEventListener("click", () => {
        mobileDrawer.classList.remove("active");
      });
    });
  }

  // Traitement du formulaire de contact / diagnostic
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
});

