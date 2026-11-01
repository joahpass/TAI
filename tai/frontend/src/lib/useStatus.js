import { useCallback, useEffect, useState } from 'react';
import { api } from '../api.js';

/** 轮询 GET /api/status。页面隐藏时暂停轮询。 */
export function useStatus(interval = 5000) {
  const [status, setStatus] = useState(null);
  const [offline, setOffline] = useState('');

  const refresh = useCallback(async () => {
    try {
      const s = await api.status();
      setStatus(s);
      setOffline('');
      return s;
    } catch (e) {
      setOffline(e.message || String(e));
      return null;
    }
  }, []);

  useEffect(() => {
    refresh();
    const t = setInterval(() => {
      if (document.visibilityState !== 'hidden') refresh();
    }, interval);
    const onVisible = () => document.visibilityState === 'visible' && refresh();
    document.addEventListener('visibilitychange', onVisible);
    return () => {
      clearInterval(t);
      document.removeEventListener('visibilitychange', onVisible);
    };
  }, [refresh, interval]);

  return { status, offline, refresh };
}
