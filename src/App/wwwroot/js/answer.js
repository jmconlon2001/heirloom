// The writing page, enhanced. Without this script the page still works as a normal form.
//  - autosave every 5 seconds and when leaving a field ("Saved ✓ 2:41 PM")
//  - a copy of the words in localStorage, so an expired session or dropped Wi-Fi never loses them
//  - chunked, resumable uploads with a big progress bar
//  - "Record my voice" with a timer and level meter; every second of a recording is kept on the
//    device (IndexedDB) until the server has it, so closing the page or losing Wi-Fi never loses it

const form = document.querySelector("[data-answer]");
if (form) init(form);

function init(form) {
  const memoryId = form.dataset.memoryId;
  const csrf = form.dataset.csrf;
  const status = form.querySelector("[data-save-status]");
  const storageKey = `fm-draft-${memoryId}`;
  let revision = Number(form.dataset.revision);
  let dirty = false;
  let saving = false;
  let activeUploads = 0;

  // ---------------------------------------------------------------- helpers
  const timeNow = () => new Date().toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
  const say = (text, kind = "") => {
    status.textContent = text;
    status.dataset.kind = kind;
  };
  const field = (name) => form.elements.namedItem(name);
  const value = (name) => field(name)?.value ?? "";
  const intOrNull = (name) => {
    const v = value(name).trim();
    return v === "" || Number.isNaN(Number(v)) ? null : Number(v);
  };

  async function api(method, url, body, extraHeaders = {}) {
    const isJson = body !== undefined && !(body instanceof Blob);
    return fetch(url, {
      method,
      credentials: "same-origin",
      headers: {
        "X-CSRF-TOKEN": csrf,
        ...(isJson ? { "Content-Type": "application/json" } : {}),
        ...extraHeaders,
      },
      body: body === undefined ? undefined : isJson ? JSON.stringify(body) : body,
    });
  }

  function snapshot() {
    const captions = {};
    for (const input of form.querySelectorAll("[data-caption]")) {
      captions[input.closest("[data-attachment-id]").dataset.attachmentId] = input.value;
    }
    const checkedValues = (selector) => [...form.querySelectorAll(selector)].filter((i) => i.checked).map((i) => i.value);
    const photoPeople = {};
    for (const marker of form.querySelectorAll('input[name="PhotoFormPresent"]')) {
      photoPeople[marker.value] = checkedValues(`input[name="PhotoPeople[${marker.value}]"]`);
    }
    const visibility = form.querySelector('input[name="Visibility"]:checked')?.value;
    return {
      ...(visibility ? { visibility, sharedWith: checkedValues('input[name="SharedWith"]') } : {}),
      ...(form.querySelector('input[name="PeopleFormPresent"]') ? { peopleIds: checkedValues('input[name="PeopleIds"]') } : {}),
      photoPeople,
      title: field("Title") ? value("Title") : null,
      body: value("Body"),
      place: value("Place"),
      when: {
        year: intOrNull("WhenYear"),
        month: intOrNull("WhenMonth"),
        day: intOrNull("WhenDay"),
        circa: field("WhenCirca")?.checked ?? false,
        decade: intOrNull("WhenDecade"),
        label: value("WhenLabel"),
      },
      captions,
    };
  }

  function keepLocalCopy() {
    try {
      localStorage.setItem(storageKey, JSON.stringify({ ...snapshot(), savedAt: new Date().toISOString() }));
    } catch {
      /* private browsing or storage full: the server copy still exists */
    }
  }

  function clearLocalCopy() {
    try {
      localStorage.removeItem(storageKey);
    } catch {
      /* ignore */
    }
  }

  // ---------------------------------------------------------------- restore after an expired session
  (function restoreLocalCopy() {
    let saved;
    try {
      saved = JSON.parse(localStorage.getItem(storageKey) ?? "null");
    } catch {
      saved = null;
    }
    if (!saved) return;
    const serverTime = Date.parse(form.dataset.updatedAt + (form.dataset.updatedAt.endsWith("Z") ? "" : "Z"));
    const differs = saved.body !== value("Body") || (saved.title ?? "") !== (field("Title") ? value("Title") : "");
    if (Date.parse(saved.savedAt) > serverTime && differs) {
      field("Body").value = saved.body;
      if (field("Title") && saved.title !== null) field("Title").value = saved.title;
      if (field("Place")) field("Place").value = saved.place ?? "";
      dirty = true;
      say("We put back the words you wrote earlier on this device.", "info");
      save();
    } else {
      clearLocalCopy();
    }
  })();

  // ---------------------------------------------------------------- autosave
  async function save() {
    if (!dirty || saving) return;
    saving = true;
    dirty = false;
    keepLocalCopy();
    say("Saving…");
    try {
      const response = await api("POST", `/api/memories/${memoryId}/autosave`, { ...snapshot(), revision });
      if (response.ok) {
        const result = await response.json();
        revision = result.revision;
        say(`Saved ✓ ${timeNow()}`, "ok");
        if (!dirty) clearLocalCopy();
      } else if (response.status === 401) {
        dirty = true;
        const back = encodeURIComponent(location.pathname);
        status.innerHTML = "";
        status.append("You've been signed out, but your words are safe on this device. ");
        const link = document.createElement("a");
        link.href = `/account/login?returnUrl=${back}`;
        link.textContent = "Sign in again";
        status.append(link, " and they'll be put back.");
        status.dataset.kind = "warn";
      } else if (response.status === 409) {
        say("This story was changed in another window or on another device. Your words here are kept on this device. Reload the page to continue.", "warn");
      } else {
        dirty = true;
        say("Not saved yet. We'll keep trying. Your words are kept on this device.", "warn");
      }
    } catch {
      dirty = true;
      say("No connection right now. Your words are kept on this device and will be saved when you're back online.", "warn");
    } finally {
      saving = false;
    }
  }

  // Anything typed before this script ran (slow device, or the browser restoring the form after
  // Back) has no input event; compare with what the server sent so it still gets saved.
  for (const el of form.querySelectorAll("textarea, input[type=text], input:not([type])")) {
    if (el.value !== el.defaultValue) dirty = true;
  }
  if (dirty) save();

  form.addEventListener("input", (e) => {
    if (e.target.matches("[data-file-input]")) return;
    dirty = true;
    keepLocalCopy();
  });
  form.addEventListener("change", (e) => {
    if (!e.target.matches("[data-file-input]")) dirty = true;
  });
  form.addEventListener("focusout", () => save());
  setInterval(save, 5000);
  window.addEventListener("online", () => save());
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "hidden") save();
  });

  // Normal Save/Finish buttons still post the form; just don't leave mid-upload.
  form.addEventListener("submit", (e) => {
    if (activeUploads > 0) {
      e.preventDefault();
      say("Please wait until your photos and recordings finish uploading.", "warn");
      return;
    }
    clearLocalCopy();
  });

  // ---------------------------------------------------------------- auto-growing text box
  for (const textarea of form.querySelectorAll("[data-autogrow]")) {
    const grow = () => {
      textarea.style.height = "auto";
      textarea.style.height = `${textarea.scrollHeight + 4}px`;
    };
    textarea.addEventListener("input", grow);
    grow();
  }

  // ---------------------------------------------------------------- uploads
  const uploadList = form.querySelector("[data-upload-list]");
  const fileInput = form.querySelector("[data-file-input]");

  fileInput?.addEventListener("change", () => {
    const files = [...fileInput.files];
    fileInput.value = ""; // so a later Save doesn't send them a second time
    for (const file of files) upload(file, file.name, false);
  });

  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

  function progressRow(name) {
    const li = document.createElement("li");
    li.className = "upload";
    const label = document.createElement("p");
    label.className = "upload-name";
    label.textContent = name;
    const bar = document.createElement("progress");
    bar.max = 100;
    bar.value = 0;
    bar.setAttribute("aria-label", `Uploading ${name}`);
    const text = document.createElement("p");
    text.className = "upload-status";
    text.textContent = "Starting…";
    li.append(label, bar, text);
    uploadList.append(li);
    return {
      progress(fraction) {
        bar.value = Math.round(fraction * 100);
        text.textContent = `${bar.value}% uploaded`;
      },
      message(msg, kind = "") {
        text.textContent = msg;
        li.dataset.kind = kind;
      },
      done() {
        li.remove();
      },
    };
  }

  const problem = {
    413: "This file is too big to add.",
    415: "This kind of file can't be added. Photos, PDFs, sound and video files work.",
  };

  // Resolves true once the server has the whole file.
  async function upload(blob, name, voice) {
    activeUploads++;
    const row = progressRow(name);
    try {
      let response = await api("POST", `/api/memories/${memoryId}/uploads`, { fileName: name, size: blob.size, voice });
      if (!response.ok) throw new UploadProblem(response.status);
      const { uploadId, chunkSize } = await response.json();

      let offset = 0;
      let failures = 0;
      while (offset < blob.size) {
        try {
          response = await api("PUT", `/api/uploads/${uploadId}?offset=${offset}`, blob.slice(offset, offset + chunkSize), {
            "Content-Type": "application/octet-stream",
          });
          if (response.ok || response.status === 409) {
            offset = (await response.json()).received; // 409: the server tells us where to carry on
            failures = 0;
            row.progress(offset / blob.size);
          } else if (response.status === 401) {
            throw new UploadProblem(401);
          } else if (problem[response.status]) {
            throw new UploadProblem(response.status);
          } else {
            throw new Error(`HTTP ${response.status}`);
          }
        } catch (err) {
          if (err instanceof UploadProblem) throw err;
          // Dropped connection: wait, ask the server how much arrived, and resume.
          failures++;
          row.message("Connection lost. Trying again…", "warn");
          await sleep(Math.min(30000, 1000 * 2 ** Math.min(failures, 5)));
          try {
            const check = await api("GET", `/api/uploads/${uploadId}`);
            if (check.ok) offset = (await check.json()).received;
          } catch {
            /* still offline; loop and wait again */
          }
        }
      }

      row.message("Finishing…");
      response = await api("POST", `/api/uploads/${uploadId}/complete`);
      if (!response.ok) throw new UploadProblem(response.status);
      row.done();
      await refreshAttachments();
      return true;
    } catch (err) {
      const status = err instanceof UploadProblem ? err.status : 0;
      row.message(
        status === 401
          ? "You've been signed out. Please sign in again and add this file once more."
          : problem[status] ?? "This file couldn't be added. Please try again.",
        "error",
      );
      return false;
    } finally {
      activeUploads--;
    }
  }

  // Don't let a page close quietly while something is still on its way to the server.
  window.addEventListener("beforeunload", (e) => {
    if (activeUploads > 0 || recordingNow) {
      e.preventDefault();
      e.returnValue = "";
    }
  });

  class UploadProblem extends Error {
    constructor(status) {
      super(`upload failed (${status})`);
      this.status = status;
    }
  }

  // Swap in the server's rendering of the photos section (with its captions and buttons),
  // keeping any caption the person is in the middle of typing.
  async function refreshAttachments() {
    const typed = new Map([...form.querySelectorAll("[data-caption]")].map((i) => [i.id, i.value]));
    const html = await (await fetch(location.pathname, { credentials: "same-origin" })).text();
    const fresh = new DOMParser().parseFromString(html, "text/html").querySelector("[data-attachments-section]");
    const current = form.querySelector("[data-attachments-section]");
    if (!fresh || !current) return;
    current.className = fresh.className;
    current.innerHTML = fresh.innerHTML;
    for (const input of current.querySelectorAll("[data-caption]")) {
      if (typed.has(input.id)) input.value = typed.get(input.id);
    }
    showDragHandles();
    pollPending();
  }

  function showDragHandles() {
    for (const handle of form.querySelectorAll("[data-drag-handle]")) handle.hidden = false;
  }
  showDragHandles();

  let polling = false;
  async function pollPending() {
    if (polling) return;
    polling = true;
    try {
      for (let i = 0; i < 150; i++) {
        const pending = [...form.querySelectorAll('[data-attachment-id][data-state="pending"], [data-attachment-id][data-transcript="pending"]')];
        if (pending.length === 0) return;
        await sleep(2000);
        const states = await Promise.all(
          pending.map(async (li) => {
            const r = await api("GET", `/api/attachments/${li.dataset.attachmentId}`);
            if (!r.ok) return "pending";
            const json = await r.json();
            return json.state === "pending" || json.transcript === "pending" ? "pending" : "done";
          }),
        );
        if (states.some((s) => s !== "pending")) {
          polling = false;
          await refreshAttachments();
          return;
        }
      }
    } finally {
      polling = false;
    }
  }
  pollPending();

  // ---------------------------------------------------------------- drag to reorder photos and recordings
  // A handle on each item: drag it with a finger or mouse, or focus it and press the arrow keys.
  // The order is saved through the same API as everything else; Move up / Move down still work without this.
  form.addEventListener("pointerdown", (e) => {
    const handle = e.target.closest("[data-drag-handle]");
    if (!handle || e.button > 0) return;
    e.preventDefault();
    const item = handle.closest("[data-attachment-id]");
    const list = item.parentElement;
    const before = order(list);
    // Listen on the window: moving the item in the page releases any pointer capture on the handle.
    item.classList.add("is-dragging");
    let lastY = e.clientY;
    let scroller = 0;

    const autoScroll = () => {
      const edge = 80;
      const step = lastY < edge ? -12 : lastY > window.innerHeight - edge ? 12 : 0;
      if (step) {
        window.scrollBy(0, step);
        moveTo(lastY);
      }
      scroller = requestAnimationFrame(autoScroll);
    };
    scroller = requestAnimationFrame(autoScroll);

    function moveTo(y) {
      for (const other of list.querySelectorAll(":scope > [data-attachment-id]")) {
        if (other === item) continue;
        const box = other.getBoundingClientRect();
        const middle = box.top + box.height / 2;
        const isAbove = other.compareDocumentPosition(item) & Node.DOCUMENT_POSITION_FOLLOWING;
        if (isAbove && y < middle) return list.insertBefore(item, other);
        if (!isAbove && y > middle) return other.after(item);
      }
    }

    const move = (ev) => {
      if (ev.pointerId !== e.pointerId) return;
      ev.preventDefault();
      lastY = ev.clientY;
      moveTo(ev.clientY);
    };
    const end = (ev) => {
      if (ev.pointerId !== e.pointerId) return;
      cancelAnimationFrame(scroller);
      item.classList.remove("is-dragging");
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", end);
      window.removeEventListener("pointercancel", end);
      if (order(list).join() !== before.join()) saveOrder(list, handle);
    };
    window.addEventListener("pointermove", move, { passive: false });
    window.addEventListener("pointerup", end);
    window.addEventListener("pointercancel", end);
  });

  form.addEventListener("keydown", (e) => {
    const handle = e.target.closest?.("[data-drag-handle]");
    if (!handle || (e.key !== "ArrowUp" && e.key !== "ArrowDown")) return;
    e.preventDefault();
    const item = handle.closest("[data-attachment-id]");
    const sibling = e.key === "ArrowUp" ? item.previousElementSibling : item.nextElementSibling;
    if (!sibling?.matches("[data-attachment-id]")) return;
    if (e.key === "ArrowUp") sibling.before(item);
    else sibling.after(item);
    saveOrder(item.parentElement, handle);
  });

  const order = (list) => [...list.querySelectorAll(":scope > [data-attachment-id]")].map((li) => li.dataset.attachmentId);

  async function saveOrder(list, handle) {
    const id = handle.closest("[data-attachment-id]").dataset.attachmentId;
    try {
      const response = await api("POST", `/api/memories/${memoryId}/attachments/order`, { ids: order(list) });
      if (!response.ok) throw new Error(String(response.status));
      say(`New order saved ✓ ${timeNow()}`, "ok");
    } catch {
      say("The new order couldn't be saved. Please try again.", "warn");
    }
    await refreshAttachments();
    form.querySelector(`[data-attachment-id="${id}"] [data-drag-handle]`)?.focus();
  }

  // ---------------------------------------------------------------- recordings kept on this device
  // One IndexedDB record per recording: { id, memoryId, name, type, chunks: [Blob] }.
  // Deleted only once the server has the whole recording.
  let recordingNow = false;
  const safekeeping = openSafekeeping();

  function openSafekeeping() {
    return new Promise((resolve) => {
      try {
        const request = indexedDB.open("fm-recordings", 1);
        request.onupgradeneeded = () => request.result.createObjectStore("recordings", { keyPath: "id" });
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => resolve(null);
      } catch {
        resolve(null); // no IndexedDB (very old browser, some private modes): record as before
      }
    });
  }

  async function keep(change) {
    const db = await safekeeping;
    if (!db) return;
    await new Promise((resolve) => {
      try {
        const tx = db.transaction("recordings", "readwrite");
        change(tx.objectStore("recordings"));
        tx.oncomplete = tx.onerror = tx.onabort = () => resolve();
      } catch {
        resolve();
      }
    });
  }

  const keepChunk = (id, chunk) =>
    keep((store) => {
      const get = store.get(id);
      get.onsuccess = () => {
        if (!get.result) return;
        get.result.chunks.push(chunk);
        store.put(get.result);
      };
    });

  async function keptRecordings() {
    const db = await safekeeping;
    if (!db) return [];
    return new Promise((resolve) => {
      try {
        const all = db.transaction("recordings").objectStore("recordings").getAll();
        all.onsuccess = () => resolve(all.result.filter((r) => r.memoryId === memoryId));
        all.onerror = () => resolve([]);
      } catch {
        resolve([]);
      }
    });
  }

  async function uploadKept(record) {
    const blob = new Blob(record.chunks, { type: record.type });
    if (blob.size === 0) return keep((store) => store.delete(record.id));
    if (await upload(blob, record.name, true)) await keep((store) => store.delete(record.id));
  }

  // A recording from an earlier visit that never reached the server: send it now.
  (async () => {
    const waiting = await keptRecordings();
    if (waiting.length === 0) return;
    say(
      waiting.length === 1
        ? "We found a recording from earlier that hadn't finished saving. Adding it to this story now."
        : `We found ${waiting.length} recordings from earlier that hadn't finished saving. Adding them to this story now.`,
      "info",
    );
    for (const record of waiting) await uploadKept(record);
  })();

  // ---------------------------------------------------------------- voice recording
  const recorder = form.querySelector("[data-recorder]");
  const canRecord = recorder && window.MediaRecorder && navigator.mediaDevices?.getUserMedia;
  if (canRecord) setUpRecorder(recorder);

  function setUpRecorder(section) {
    section.hidden = false;
    const startButton = section.querySelector("[data-record-start]");
    const recordingPanel = section.querySelector("[data-recording]");
    const stopButton = section.querySelector("[data-record-stop]");
    const timeText = section.querySelector("[data-record-time]");
    const level = section.querySelector("[data-record-level]");
    const errorText = section.querySelector("[data-record-error]");

    let mediaRecorder;
    let stream;
    let chunks = [];
    let timer;
    let audioContext;
    let started;

    const pickType = () =>
      ["audio/mp4", "audio/webm;codecs=opus", "audio/webm", "audio/ogg;codecs=opus"].find((t) => MediaRecorder.isTypeSupported?.(t)) ?? "";

    startButton.addEventListener("click", async () => {
      errorText.hidden = true;
      try {
        stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      } catch (err) {
        errorText.textContent =
          err?.name === "NotAllowedError"
            ? "To record, this website needs permission to use the microphone. On an iPad, open Settings, then Safari, then Microphone, and choose Allow."
            : "We couldn't find a microphone on this device.";
        errorText.hidden = false;
        return;
      }

      const type = pickType();
      mediaRecorder = new MediaRecorder(stream, type ? { mimeType: type } : undefined);
      chunks = [];
      const actualType = mediaRecorder.mimeType || type || "audio/webm";
      const ext = actualType.includes("mp4") ? "m4a" : actualType.includes("ogg") ? "ogg" : "webm";
      const record = { id: `${memoryId}-${Date.now()}`, memoryId, name: `Voice recording ${new Date().toLocaleString()}.${ext}`, type: actualType, chunks: [] };
      await keep((store) => store.put(record));
      mediaRecorder.addEventListener("dataavailable", (e) => {
        if (e.data.size === 0) return;
        chunks.push(e.data);
        keepChunk(record.id, e.data);
      });
      mediaRecorder.addEventListener("stop", () => finish(record));
      mediaRecorder.start(1000);
      recordingNow = true;

      started = Date.now();
      startButton.hidden = true;
      recordingPanel.hidden = false;
      stopButton.focus();
      timer = setInterval(() => {
        const s = Math.floor((Date.now() - started) / 1000);
        timeText.textContent = `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
      }, 250);
      showLevel();
    });

    stopButton.addEventListener("click", () => mediaRecorder?.state === "recording" && mediaRecorder.stop());

    function showLevel() {
      try {
        audioContext = new (window.AudioContext || window.webkitAudioContext)();
        const analyser = audioContext.createAnalyser();
        analyser.fftSize = 512;
        audioContext.createMediaStreamSource(stream).connect(analyser);
        const data = new Uint8Array(analyser.fftSize);
        const draw = () => {
          if (!mediaRecorder || mediaRecorder.state !== "recording") return;
          analyser.getByteTimeDomainData(data);
          let peak = 0;
          for (const v of data) peak = Math.max(peak, Math.abs(v - 128));
          level.style.width = `${Math.min(100, (peak / 128) * 160)}%`;
          requestAnimationFrame(draw);
        };
        draw();
      } catch {
        /* the level meter is a nicety; recording still works */
      }
    }

    async function finish(record) {
      recordingNow = false;
      clearInterval(timer);
      stream.getTracks().forEach((t) => t.stop());
      audioContext?.close();
      recordingPanel.hidden = true;
      startButton.hidden = false;
      level.style.width = "0";
      timeText.textContent = "0:00";

      startButton.focus();
      const blob = new Blob(chunks, { type: record.type });
      if (blob.size === 0) {
        await keep((store) => store.delete(record.id));
        return;
      }
      // The device copy is deleted only once the server has the whole recording.
      if (await upload(blob, record.name, true)) await keep((store) => store.delete(record.id));
      else say("Your recording is kept on this device. It will be added when you come back to this page.", "warn");
    }
  }
}
