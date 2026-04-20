/**
 * Lazy load non-critical fonts
 */
export function loadFontsLazily() {
  if (typeof window !== "undefined") {
    // Load heavy Uthman bold font only when needed
    const loadUthmanBold = () => {
      const link = document.createElement("link");
      link.rel = "stylesheet";
      link.href = "/assets/uthman/uthman.css";
      link.media = "print";
      document.head.appendChild(link);
    };

    // Load fonts when page is idle
    if ("requestIdleCallback" in window) {
      requestIdleCallback(() => {
        loadUthmanBold();
      });
    } else {
      // Fallback for browsers without requestIdleCallback
      setTimeout(() => {
        loadUthmanBold();
      }, 1000);
    }
  }
}
