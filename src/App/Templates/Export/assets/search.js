// Offline search for the exported family website: works from a USB stick (file://), so it uses a
// plain <script> with the index (assets/search-index.js) instead of fetch() or modules.
(function () {
  var params = new URLSearchParams(window.location.search);
  var q = (params.get("q") || "").trim();
  var input = document.getElementById("q");
  var status = document.getElementById("search-status");
  var list = document.getElementById("search-results");
  input.value = q;
  if (!q) { status.textContent = "Type a word and press Search."; return; }
  var words = q.toLowerCase().split(/\s+/).filter(Boolean);
  var hits = (window.SEARCH_INDEX || []).filter(function (s) {
    var hay = (s.title + " " + s.teller + " " + s.people + " " + s.text).toLowerCase();
    return words.every(function (w) { return hay.indexOf(w) >= 0; });
  });
  status.textContent = hits.length === 0 ? "Nothing found for “" + q + "”." : hits.length + (hits.length === 1 ? " story" : " stories") + " found for “" + q + "”.";
  hits.forEach(function (s) {
    var li = document.createElement("li");
    var a = document.createElement("a");
    a.href = s.url;
    a.textContent = s.title;
    var meta = document.createElement("span");
    meta.className = "card-meta";
    meta.textContent = " · " + s.teller;
    li.appendChild(a);
    li.appendChild(meta);
    list.appendChild(li);
  });
})();
