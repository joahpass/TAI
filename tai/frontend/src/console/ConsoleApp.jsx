import { useEffect } from 'react';
import { useStatus } from '../lib/useStatus.js';
import { cx } from '../utils.js';
import ConfigSection from './ConfigSection.jsx';
import PromptSection from './PromptSection.jsx';
import StatusSection from './StatusSection.jsx';
import UpdateSection from '../components/UpdateSection.jsx';
import ToolTester from './ToolTester.jsx';

/** 配置与诊断控制台（路径 /）。不包含助手对话。 */
export default function ConsoleApp() {
  const { status, offline, refresh } = useStatus(5000);

  useEffect(() => {
    document.title = 'TAI 配置与诊断';
  }, []);

  const connected = Boolean(status?.mcp?.connected);
  return (
    <div className="cs-app">
      <header className="cs-head">
        <div className="cs-brand"><strong>TAI</strong><span>配置与诊断</span></div>
        <span className="spacer" />
        <span className={cx('chip', offline ? 'chip-off' : connected ? 'chip-ok' : 'chip-warn')}>
          <span className={cx('dot', offline ? 'dot-off' : connected ? 'dot-ok' : 'dot-warn')} />
          {offline ? '服务离线' : connected ? '博途已连接' : '博途未连接'}
        </span>
        <a className="btn btn-sm" href="/workspace">打开设计工作台</a>
      </header>
      <main className="cs-main">
        <div className="cs-col">
          <ConfigSection busy={Boolean(status?.busy)} onSaved={refresh} />
          <PromptSection busy={Boolean(status?.busy)} />
        </div>
        <div className="cs-col">
          <UpdateSection />
          <StatusSection status={status} offline={offline} onRefresh={refresh} />
          <ToolTester busy={Boolean(status?.busy)} offline={offline} onDone={refresh} />
        </div>
      </main>
    </div>
  );
}
