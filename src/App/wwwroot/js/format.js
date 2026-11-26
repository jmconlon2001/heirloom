// Formatting for the story box (D-078): wraps the selection in the same plain-text marks people
// know from email, and places photos and recordings at the cursor. The textarea stays the source
// of truth, so autosave, the keep-on-device copy and Simple view all work exactly as before.

const textarea = document.querySelector("textarea.story-text");
const bar = document.querySelector("[data-format-bar]");

function changed() {
  // answer.js autosaves on input events; programmatic edits don't fire one by themselves.
  textarea.dispatchEvent(new Event("input", { bubbles: true }));
  textarea.focus();
}

function replace(start, end, text, selectFrom, selectTo) {
  textarea.setRangeText(text, start, end, "end");
  if (selectFrom !== undefined) textarea.setSelectionRange(selectFrom, selectTo);
  changed();
}

function wrap(mark, placeholder) {
  const { selectionStart: s, selectionEnd: e, value } = textarea;
  const chosen = value.slice(s, e) || placeholder;
  replace(s, e, mark + chosen + mark, s + mark.length, s + mark.length + chosen.length);
}

// Prefix every line the selection touches ("## ", "- ", "> ").
function prefixLines(prefix) {
  const { selectionStart: s, selectionEnd: e, value } = textarea;
  const from = value.lastIndexOf("\n", s - 1) + 1;
  const toBreak = value.indexOf("\n", e);
  const to = toBreak === -1 ? value.length : toBreak;
  const lines = value.slice(from, to).split("\n");
  const allHave = lines.every((l) => l.startsWith(prefix));
  const text = lines.map((l) => (allHave ? l.slice(prefix.length) : l.length ? prefix + l : l)).join("\n");
  replace(from, to, text, from, from + text.length);
}

function link() {
  const url = window.prompt("Paste the web address (starting with https://)");
  if (!url) return;
  if (!/^(https?:\/\/|mailto:)\S+$/i.test(url.trim())) {
    window.alert("That doesn't look like a web address. It should start with https://");
    return;
  }
  const { selectionStart: s, selectionEnd: e, value } = textarea;
  const words = value.slice(s, e) || "link";
  const text = `[${words}](${url.trim()})`;
  replace(s, e, text, s + 1, s + 1 + words.length);
}

// "Place in the story": the media line goes on a line of its own at the cursor.
function placeMedia(id, caption) {
  const { selectionStart: s, value } = textarea;
  const marker = `![${(caption || "").replace(/[\]\n]/g, " ").trim()}](media:${id})`;
  if (value.includes(`(media:${id})`)) {
    window.alert("That's already in the story. Move its line to change where it goes.");
    return;
  }
  const before = value.slice(0, s).replace(/\s*$/, "");
  const after = value.slice(s).replace(/^\s*/, "");
  const text = (before ? before + "\n\n" : "") + marker + "\n\n" + after;
  textarea.value = text;
  const caret = (before ? before.length + 2 : 0) + marker.length + 2;
  textarea.setSelectionRange(caret, caret);
  changed();
}

if (textarea && bar) {
  bar.hidden = false;
  bar.addEventListener("click", (event) => {
    const button = event.target.closest("[data-format]");
    if (!button) return;
    switch (button.dataset.format) {
      case "bold": wrap("**", "bold words"); break;
      case "italic": wrap("*", "words"); break;
      case "heading": prefixLines("## "); break;
      case "list": prefixLines("- "); break;
      case "quote": prefixLines("> "); break;
      case "link": link(); break;
    }
  });
}

if (textarea) {
  // Event delegation: the attachments section is re-rendered after each upload.
  const showPlaceButtons = () => document.querySelectorAll("[data-place-media]").forEach((b) => (b.hidden = false));
  showPlaceButtons();
  new MutationObserver(showPlaceButtons).observe(document.body, { childList: true, subtree: true });
  document.addEventListener("click", (event) => {
    const button = event.target.closest("[data-place-media]");
    if (!button) return;
    const caption = document.getElementById(`caption-${button.dataset.placeMedia}`)?.value ?? button.dataset.placeCaption;
    placeMedia(button.dataset.placeMedia, caption);
  });
}
