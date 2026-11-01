// NDJSON 增量解析器：可处理任意 chunk 分割（包括被截断在多字节 UTF-8 字符中间、
// 一个 chunk 含多行、一行跨多个 chunk、\r\n 换行、末尾无换行等情况）。

/**
 * @param {(event: any) => void} onEvent 每解析出一行合法 JSON 调用一次
 * @param {(line: string, error: unknown) => void} [onInvalid] 无法解析的行
 */
export function createNdjsonParser(onEvent, onInvalid) {
  const decoder = new TextDecoder('utf-8');
  let buffer = '';

  function handleLine(line) {
    const trimmed = line.trim(); // 同时去掉 \r
    if (!trimmed) return;
    let value;
    try {
      value = JSON.parse(trimmed);
    } catch (error) {
      if (onInvalid) onInvalid(trimmed, error);
      return;
    }
    onEvent(value);
  }

  function drain() {
    let index = buffer.indexOf('\n');
    while (index !== -1) {
      const line = buffer.slice(0, index);
      buffer = buffer.slice(index + 1);
      handleLine(line);
      index = buffer.indexOf('\n');
    }
  }

  return {
    /** @param {Uint8Array | string} chunk */
    push(chunk) {
      if (chunk == null) return;
      buffer += typeof chunk === 'string' ? chunk : decoder.decode(chunk, { stream: true });
      drain();
    },
    /** 流结束：冲刷解码器并处理最后一行（可能没有换行符）。 */
    end() {
      buffer += decoder.decode();
      drain();
      const rest = buffer;
      buffer = '';
      handleLine(rest);
    },
  };
}

/**
 * 读取 ReadableStream<Uint8Array> 中的 NDJSON 事件。
 * 被 AbortSignal 中止时 reader.read() 会抛出 AbortError，由调用方处理。
 */
export async function readNdjsonStream(stream, onEvent, onInvalid) {
  const parser = createNdjsonParser(onEvent, onInvalid);
  const reader = stream.getReader();
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      if (value && value.length) parser.push(value);
    }
    parser.end();
  } finally {
    try {
      reader.releaseLock();
    } catch {
      /* 已释放 */
    }
  }
}
