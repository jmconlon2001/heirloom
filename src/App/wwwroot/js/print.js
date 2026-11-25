// "Print this card" button (no inline scripts allowed by our CSP).
for (const button of document.querySelectorAll("[data-print]")) {
  button.addEventListener("click", () => window.print());
}
