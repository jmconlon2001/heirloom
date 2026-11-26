// Progressive enhancement for the text-size and high-contrast buttons.
// Without JavaScript the buttons post a form and the page reloads; with it,
// the change applies instantly and is saved in the same cookie the server reads.

const COOKIE = "fm_display";

function readPrefs() {
  const match = document.cookie.match(/(?:^|;\s*)fm_display=([^;]*)/);
  // No choice saved on this device yet: start from what the server drew (the family's starting display).
  const html = document.documentElement;
  const prefs = {
    size: [0, 1, 2].find((n) => html.classList.contains(`text-${n}`)) ?? 0,
    contrast: html.classList.contains("high-contrast"),
  };
  if (!match) return prefs;
  for (const part of decodeURIComponent(match[1]).split(".")) {
    if (/^s[0-2]$/.test(part)) prefs.size = Number(part[1]);
    if (part === "c1") prefs.contrast = true;
  }
  return prefs;
}

function writePrefs(prefs) {
  const value = `s${prefs.size}.c${prefs.contrast ? 1 : 0}`;
  const secure = location.protocol === "https:" ? "; Secure" : "";
  document.cookie = `${COOKIE}=${value}; Path=/; Max-Age=${60 * 60 * 24 * 365 * 5}; SameSite=Lax${secure}`;
}

function apply(form, prefs) {
  const html = document.documentElement;
  html.classList.remove("text-0", "text-1", "text-2");
  html.classList.add(`text-${prefs.size}`);
  html.classList.toggle("high-contrast", prefs.contrast);

  for (const button of form.querySelectorAll("[data-size]")) {
    button.setAttribute("aria-pressed", String(Number(button.dataset.size) === prefs.size));
  }
  const contrast = form.querySelector("[data-contrast]");
  if (contrast) {
    contrast.setAttribute("aria-pressed", String(prefs.contrast));
    contrast.value = prefs.contrast ? "off" : "on";
  }
}

for (const form of document.querySelectorAll("[data-display-controls]")) {
  form.addEventListener("submit", (event) => {
    const button = event.submitter;
    if (!button) return;
    event.preventDefault();

    const prefs = readPrefs();
    if (button.dataset.size !== undefined) prefs.size = Number(button.dataset.size);
    if (button.hasAttribute("data-contrast")) prefs.contrast = !prefs.contrast;

    writePrefs(prefs);
    apply(form, prefs);
  });
}
