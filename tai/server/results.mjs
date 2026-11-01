// Interpret MCP envelopes without mistaking transport success for engineering success.
export function resultData(result) {
  if (result?.structuredContent) return result.structuredContent;
  for (const item of result?.content ?? []) {
    if (item.type === 'text') { try { return JSON.parse(item.text); } catch {} }
  }
  return null;
}
export function engineeringFailure(result) {
  if (result?.isError) return true;
  const data = resultData(result);
  if (!data || typeof data !== 'object') return false;
  const meta = data.Meta ?? data.meta;
  return (data.Succeeded ?? data.succeeded) === false || meta?.success === false ||
    Number(data.ErrorCount ?? data.errorCount ?? 0) > 0;
}
export function checkedResult(result) {
  return engineeringFailure(result) ? {...result, isError:true} : result;
}
