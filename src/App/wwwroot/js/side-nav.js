// Detailed view: on phones the sidebar folds into a "Menu" button so the page
// content is on the first screen. Without JavaScript it simply stays open.
const narrow = window.matchMedia("(max-width: 48rem)");
const menus = document.querySelectorAll("[data-side-nav]");
const sync = () => { for (const menu of menus) menu.open = !narrow.matches; };
sync();
narrow.addEventListener("change", sync);
