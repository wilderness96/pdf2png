/* PDF → PNG single-page app. Vanilla JS, no dependencies, no build step. */
(() => {
  "use strict";

  const $ = (sel) => document.querySelector(sel);

  const uploadView = $("#upload-view");
  const viewerView = $("#viewer-view");
  const dropzone = $("#dropzone");
  const fileInput = $("#file-input");
  const dzName = $("#dz-name");
  const browseBtn = $("#browse-btn");
  const convertBtn = $("#convert-btn");
  const dpiSel = $("#dpi");
  const statusEl = $("#status");
  const thumbsEl = $("#thumbs");
  const bannersEl = $("#banners");
  const linksEl = $("#links");
  const newBtn = $("#new-btn");

  const state = { job: null, idx: 0 };
  let selectedFile = null;

  /* ---------------- upload ---------------- */

  function setFile(f) {
    selectedFile = f;
    dzName.textContent = `${f.name} (${formatBytes(f.size)})`;
    dzName.hidden = false;
    dropzone.classList.add("has-file");
    convertBtn.disabled = false;
    clearStatus();
  }

  fileInput.addEventListener("change", () => {
    if (fileInput.files[0]) setFile(fileInput.files[0]);
  });
  browseBtn.addEventListener("click", () => fileInput.click());

  ["dragenter", "dragover"].forEach((ev) =>
    dropzone.addEventListener(ev, (e) => {
      e.preventDefault();
      dropzone.classList.add("over");
    })
  );
  ["dragleave", "drop"].forEach((ev) =>
    dropzone.addEventListener(ev, (e) => {
      e.preventDefault();
      dropzone.classList.remove("over");
    })
  );
  dropzone.addEventListener("drop", (e) => {
    const f = e.dataTransfer && e.dataTransfer.files[0];
    if (f) setFile(f);
  });
  // don't let the browser navigate away when a file misses the dropzone
  document.addEventListener("dragover", (e) => e.preventDefault());
  document.addEventListener("drop", (e) => e.preventDefault());

  convertBtn.addEventListener("click", async () => {
    if (!selectedFile) return;
    convertBtn.disabled = true;
    setStatus("busy", "Converting…");
    try {
      const fd = new FormData();
      fd.append("file", selectedFile);
      fd.append("dpi", dpiSel.value);
      const res = await fetch("/convert", { method: "POST", body: fd });
      let data = {};
      try { data = await res.json(); } catch (_) { /* ignore */ }
      if (!res.ok || data.error) {
        throw new Error(data.error || `Conversion failed (HTTP ${res.status})`);
      }
      const job = await (await fetch(`/api/job/${data.jobId}`)).json();
      showViewer(job);
    } catch (err) {
      setStatus("error", err.message || String(err));
      convertBtn.disabled = false;
    }
  });

  function setStatus(kind, text) {
    statusEl.hidden = false;
    statusEl.className = `status ${kind}`;
    statusEl.innerHTML = "";
    if (kind === "busy") {
      const sp = document.createElement("span");
      sp.className = "spinner";
      statusEl.appendChild(sp);
    }
    statusEl.appendChild(document.createTextNode(text));
  }
  function clearStatus() {
    statusEl.hidden = true;
    statusEl.innerHTML = "";
  }

  /* ---------------- viewer ---------------- */

  function pageUrl(file) {
    return `/view/${state.job.jobId}/${file}`;
  }

  function showViewer(job) {
    state.job = job;
    state.idx = 0;
    uploadView.hidden = true;
    viewerView.hidden = false;
    document.title = `PDF → PNG · ${job.pages.length} page${job.pages.length === 1 ? "" : "s"}`;

    const linkCount = Object.values(job.links).reduce((a, l) => a + l.length, 0);
    const bits = [`${job.pages.length} page${job.pages.length === 1 ? "" : "s"}`];
    if (linkCount) bits.push(`${linkCount} link${linkCount === 1 ? "" : "s"}`);
    $("#job-meta").textContent = bits.join(" · ");

    renderBanners(job);

    thumbsEl.innerHTML = "";
    job.pages.forEach((file, i) => {
      const a = document.createElement("a");
      a.className = "thumb";
      a.href = pageUrl(file);
      a.title = `Page ${i + 1}`;
      const img = document.createElement("img");
      img.loading = "lazy";
      img.src = pageUrl(file);
      img.alt = `Page ${i + 1}`;
      const badge = document.createElement("span");
      badge.textContent = String(i + 1);
      a.append(img, badge);
      a.addEventListener("click", (e) => {
        e.preventDefault();
        select(i);
      });
      thumbsEl.appendChild(a);
    });

    select(0);
  }

  function renderBanners(job) {
    bannersEl.innerHTML = "";
    if (job.javascript && job.javascript.length) {
      const b = el("div", "banner warn");
      const icon = el("span", "b-icon");
      icon.textContent = "⚠";
      const body = el("div");
      const head = el("div");
      head.textContent = `JavaScript embedded in this PDF (${job.javascript.length} finding${job.javascript.length === 1 ? "" : "s"}) — parsed and shown below, never executed:`;
      const pre = el("pre");
      pre.textContent = job.javascript.join("\n");
      body.append(head, pre);
      b.append(icon, body);
      bannersEl.appendChild(b);
    }
    if (job.error) {
      const b = el("div", "banner danger");
      const icon = el("span", "b-icon");
      icon.textContent = "✕";
      const body = el("div");
      const head = el("div");
      head.textContent = "Conversion problem:";
      const pre = el("pre");
      pre.textContent = job.error;
      body.append(head, pre);
      b.append(icon, body);
      bannersEl.appendChild(b);
    }
  }

  function select(i) {
    const job = state.job;
    const n = job.pages.length;
    if (!n) return;
    state.idx = Math.max(0, Math.min(i, n - 1));
    const file = job.pages[state.idx];

    $("#page-img").src = pageUrl(file);
    $("#page-counter").textContent = `${state.idx + 1} / ${n}`;
    $("#prev-btn").disabled = state.idx === 0;
    $("#next-btn").disabled = state.idx === n - 1;
    $("#download-btn").href = pageUrl(file) + "?dl=1";

    // links for this page
    const urls = job.links[String(state.idx + 1)] || [];
    linksEl.innerHTML = "";
    if (!urls.length) {
      const m = el("span", "muted");
      m.textContent = "No embedded links on this page.";
      linksEl.appendChild(m);
    } else {
      for (const u of urls) {
        const c = el("span", "chip");
        c.textContent = u;
        c.title = u;
        linksEl.appendChild(c);
      }
    }

    // active thumbnail
    const thumbs = thumbsEl.children;
    for (let k = 0; k < thumbs.length; k++) {
      thumbs[k].classList.toggle("active", k === state.idx);
    }
    const active = thumbs[state.idx];
    if (active) active.scrollIntoView({ block: "nearest" });
  }

  $("#prev-btn").addEventListener("click", () => select(state.idx - 1));
  $("#next-btn").addEventListener("click", () => select(state.idx + 1));
  document.addEventListener("keydown", (e) => {
    if (viewerView.hidden) return;
    if (e.key === "ArrowLeft") select(state.idx - 1);
    else if (e.key === "ArrowRight") select(state.idx + 1);
  });

  /* ---------------- reset ---------------- */

  newBtn.addEventListener("click", () => {
    state.job = null;
    state.idx = 0;
    selectedFile = null;
    fileInput.value = "";
    viewerView.hidden = true;
    uploadView.hidden = false;
    dzName.hidden = true;
    dropzone.classList.remove("has-file", "over");
    convertBtn.disabled = true;
    clearStatus();
    document.title = "PDF → PNG";
  });

  /* ---------------- utils ---------------- */

  function el(tag, className) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    return node;
  }

  function formatBytes(b) {
    if (b < 1024) return `${b} B`;
    if (b < 1024 * 1024) return `${(b / 1024).toFixed(1)} KB`;
    return `${(b / (1024 * 1024)).toFixed(1)} MB`;
  }
})();
