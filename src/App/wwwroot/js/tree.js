// The family tree diagram (family-chart + d3, vendored in /lib). The list below the diagram
// shows the same family as plain links for screen readers and anyone who prefers lists.
const container = document.querySelector("[data-tree]");
if (container && window.f3) draw(container);

async function draw(el) {
  const response = await fetch("/api/tree", { credentials: "same-origin" });
  if (!response.ok) return;
  const data = await response.json();
  if (data.length === 0) return;

  const chart = window.f3.createChart(el, data)
    .setTransitionTime(400)
    .setCardXSpacing(260)
    .setCardYSpacing(170)
    .setShowSiblingsOfMain(true);

  const card = chart.setCardHtml()
    .setCardDisplay([["name"], ["years"]])
    .setCardImageField("avatar")
    .setMiniTree(true);

  const openLink = document.querySelector("[data-open-person]");
  card.setOnCardClick((e, d) => {
    chart.updateMainId(d.data.id);
    chart.updateTree({ tree_position: el.clientWidth < 600 ? "main_to_middle" : "fit" });
    if (openLink) {
      openLink.href = `/people/${d.data.id}`;
      openLink.textContent = `Open ${d.data.data.name}'s page`;
    }
  });

  chart.updateMainId(el.dataset.focus);
  // On a phone, fitting the whole family makes every name too small to read: start at full size
  // on the chosen person instead (drag or use the buttons to see more).
  const narrow = el.clientWidth < 600;
  chart.updateTree({ initial: true, tree_position: narrow ? "main_to_middle" : "fit" });

  const zoom = window.f3.handlers?.manualZoom ?? window.f3.manualZoom;
  for (const button of document.querySelectorAll("[data-zoom]")) {
    button.addEventListener("click", () => {
      if (zoom) zoom({ amount: Number(button.dataset.zoom), svg: chart.svg });
    });
  }
}
