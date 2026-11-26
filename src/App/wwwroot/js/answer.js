// The writing page, enhanced. Without this script the page still works as a normal form.
//  - autosave every 5 seconds and when leaving a field ("Saved ✓ 2:41 PM")
//  - a copy of the words in localStorage, so an expired session or dropped Wi-Fi never loses them
//  - chunked, resumable uploads with a big progress bar
//  - "Record my voice" with a timer and level meter

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
    } catch (err) {
      const status = err instanceof UploadProblem ? err.status : 0;
      row.message(
        status === 401
          ? "You've been signed out. Please sign in again and add this file once more."
          : problem[status] ?? "This file couldn't be added. Please try again.",
        "error",
      );
    } finally {
      activeUploads--;
    }
  }

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
    pollPending();
  }

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
      mediaRecorder.addEventListener("dataavailable", (e) => e.data.size > 0 && chunks.push(e.data));
      mediaRecorder.addEventListener("stop", finish);
      mediaRecorder.start(1000);

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

    function finish() {
      clearInterval(timer);
      stream.getTracks().forEach((t) => t.stop());
      audioContext?.close();
      recordingPanel.hidden = true;
      startButton.hidden = false;
      level.style.width = "0";
      timeText.textContent = "0:00";

      const type = mediaRecorder.mimeType || "audio/webm";
      const blob = new Blob(chunks, { type });
      if (blob.size === 0) return;
      const ext = type.includes("mp4") ? "m4a" : type.includes("ogg") ? "ogg" : "webm";
      upload(blob, `Voice recording ${new Date().toLocaleString()}.${ext}`, true);
      startButton.focus();
    }
  }
}
