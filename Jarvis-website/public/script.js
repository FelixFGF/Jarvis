(() => {
  const toggle = document.querySelector('.menu-toggle');
  const links = document.querySelector('.nav-links');
  if (toggle && links) {
    toggle.addEventListener('click', () => {
      const isOpen = toggle.getAttribute('aria-expanded') === 'true';
      toggle.setAttribute('aria-expanded', String(!isOpen));
      toggle.setAttribute('aria-label', isOpen ? 'Open navigation' : 'Close navigation');
      links.classList.toggle('open', !isOpen);
    });
    links.querySelectorAll('a').forEach((link) => link.addEventListener('click', () => {
      toggle.setAttribute('aria-expanded', 'false');
      toggle.setAttribute('aria-label', 'Open navigation');
      links.classList.remove('open');
    }));
  }

  document.querySelectorAll('.faq-item').forEach((item) => {
    item.addEventListener('toggle', () => {
      if (!item.open) return;
      document.querySelectorAll('.faq-item').forEach((other) => {
        if (other !== item) other.open = false;
      });
    });
  });

  const year = document.getElementById('year');
  if (year) year.textContent = new Date().getFullYear();

  const betaModal = document.getElementById('beta-modal');
  const downloadAnywayLink = document.getElementById('beta-download-anyway');
  const betaDiscordLink = betaModal && betaModal.querySelector('.beta-modal-discord');
  let lastDownloadTrigger = null;

  // Keep the existing private click statistics, but record a download only
  // after the visitor confirms with "Download Anyway" in the beta dialog.
  const recordDownloadClick = (version) => {
    const params = new URLSearchParams({ version });
    if (window.crypto && typeof window.crypto.randomUUID === 'function') {
      params.set('eventId', window.crypto.randomUUID());
    }
    const trackingUrl = `/api/download?${params.toString()}`;
    let queued = false;
    try {
      if (navigator.sendBeacon) {
        queued = navigator.sendBeacon(trackingUrl, new Blob([], { type: 'text/plain' }));
      }
    } catch (_) {
      // Fall through to a keepalive fetch where sendBeacon is unavailable.
    }
    if (!queued) {
      try {
        fetch(trackingUrl, { method: 'POST', keepalive: true, credentials: 'same-origin' }).catch(() => {});
      } catch (_) {
        // The direct GitHub link remains usable even if tracking is unavailable.
      }
    }
  };

  // All regular download buttons open the same beta notice before navigating.
  document.querySelectorAll('a.jarvis-download').forEach((link) => {
    link.addEventListener('click', (event) => {
      if (event.defaultPrevented || event.button !== 0) return;
      event.preventDefault();
      if (!betaModal || typeof betaModal.showModal !== 'function') {
        // Preserve a working download on browsers without native dialog support.
        window.location.href = link.href;
        return;
      }
      lastDownloadTrigger = link;
      betaModal.showModal();
      if (betaDiscordLink) betaDiscordLink.focus();
    });
  });

  if (downloadAnywayLink) {
    downloadAnywayLink.addEventListener('click', () => {
      recordDownloadClick(downloadAnywayLink.dataset.jarvisVersion || '1.1.0');
      if (betaModal && betaModal.open) betaModal.close();
    });
  }

  if (betaModal) {
    const closeButton = betaModal.querySelector('.beta-modal-close');
    if (closeButton) closeButton.addEventListener('click', () => betaModal.close());
    betaModal.addEventListener('click', (event) => {
      if (event.target === betaModal) betaModal.close();
    });
    betaModal.addEventListener('close', () => {
      if (lastDownloadTrigger && document.contains(lastDownloadTrigger)) lastDownloadTrigger.focus();
    });
    if (betaDiscordLink) betaDiscordLink.addEventListener('click', () => betaModal.close());
  }

})();
