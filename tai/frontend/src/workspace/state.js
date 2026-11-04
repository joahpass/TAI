import { normalizeDraft } from '../lib/taskOptions.js';
// Persisted per-window navigation. Server records retain their original IDs.
export const EMPTY_VIEW = { tabs: [], activeId: '', projectId: 'default', drafts: {}, homeDrafts: {}, workspacePages: {}, positions: {}, details: false, sidebar: true, theme: 'light', version: 2 };
export function restoreView(value) {
  const source = value && typeof value === 'object' ? value : {};
  const tabs = [...new Set((Array.isArray(source.tabs) ? source.tabs : []).filter(id => typeof id === 'string' && id))];
  const object = key => source[key] && typeof source[key] === 'object' && !Array.isArray(source[key]) ? source[key] : {};
  return { ...EMPTY_VIEW, ...source, version: 2, tabs,
    activeId: source.activeId === '' ? '' : tabs.includes(source.activeId) ? source.activeId : tabs[0] || '',
    projectId: typeof source.projectId === 'string' ? source.projectId : 'default',
    drafts: Object.fromEntries(Object.entries(object('drafts')).map(([id, draft]) => [id, normalizeDraft(draft)])), homeDrafts: Object.fromEntries(Object.entries(object('homeDrafts')).map(([id, draft]) => [id, normalizeDraft(draft)])), workspacePages: object('workspacePages'), positions: object('positions'),
    theme: ['light', 'dark', 'system'].includes(source.theme) ? source.theme : 'light' };
}
export function openTab(view, id, projectId = view.projectId) {
  return { ...view, tabs: view.tabs.includes(id) ? view.tabs : [...view.tabs, id], activeId: id, projectId,
    workspacePages: { ...view.workspacePages, [projectId]: id } };
}
export function showHome(view, projectId = view.projectId) {
  return { ...view, activeId: '', projectId, workspacePages: { ...view.workspacePages, [projectId]: '' } };
}
export function selectWorkspace(view, projectId, conversations) {
  const id = view.workspacePages[projectId];
  return id && conversations.some(c => c.id === id && c.projectId === projectId) ? openTab(view, id, projectId) : showHome(view, projectId);
}
export function closeTab(view, id) {
  const index = view.tabs.indexOf(id), tabs = view.tabs.filter(t => t !== id);
  const activeId = view.activeId === id ? tabs[Math.min(index, tabs.length - 1)] || '' : view.activeId;
  const workspacePages = Object.fromEntries(Object.entries(view.workspacePages || {}).map(([key, value]) => [key, value === id ? '' : value]));
  return { ...view, tabs, activeId, workspacePages };
}
export function editDraft(view, id, change) {
  return { ...view, drafts: { ...view.drafts, [id]: normalizeDraft({ ...view.drafts[id], ...change }) } };
}
export function editHomeDraft(view, projectId, change) {
  return { ...view, homeDrafts: { ...view.homeDrafts, [projectId]: normalizeDraft({ ...view.homeDrafts[projectId], ...change }) } };
}
export function sortedDesigns(items) {
  return [...items].sort((a, b) => Number(Boolean(b.pinned)) - Number(Boolean(a.pinned)) || String(b.updatedAt).localeCompare(String(a.updatedAt)));
}
