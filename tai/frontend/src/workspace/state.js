// Serializable window state. Drafts are keyed by design, never by the selected tab.
export const EMPTY_VIEW = { tabs: [], activeId: '', projectId: 'default', drafts: {}, positions: {}, details: false, sidebar: true, theme: 'light' };
export function restoreView(value) {
  if (!value || typeof value !== 'object') return { ...EMPTY_VIEW };
  const tabs = [...new Set((Array.isArray(value.tabs) ? value.tabs : []).filter(id => typeof id === 'string'))];
  return { ...EMPTY_VIEW, ...value, tabs, activeId: tabs.includes(value.activeId) ? value.activeId : tabs[0] || '',
    drafts: value.drafts && typeof value.drafts === 'object' ? value.drafts : {},
    positions: value.positions && typeof value.positions === 'object' ? value.positions : {},
    theme: ['light', 'dark', 'system'].includes(value.theme) ? value.theme : 'light' };
}
export function openTab(view, id) { return { ...view, tabs: view.tabs.includes(id) ? view.tabs : [...view.tabs, id], activeId: id }; }
export function closeTab(view, id) {
  const index = view.tabs.indexOf(id), tabs = view.tabs.filter(t => t !== id);
  return { ...view, tabs, activeId: view.activeId === id ? tabs[Math.min(index, tabs.length - 1)] || '' : view.activeId };
}
export function editDraft(view, id, change) {
  return { ...view, drafts: { ...view.drafts, [id]: { text: '', mode: 'read', ...view.drafts[id], ...change } } };
}
export function sortedDesigns(items) {
  return [...items].sort((a, b) => Number(Boolean(b.pinned)) - Number(Boolean(a.pinned)) || String(b.updatedAt).localeCompare(String(a.updatedAt)));
}
