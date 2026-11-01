import ConsoleApp from './console/ConsoleApp.jsx';
import WorkspaceApp from './workspace/WorkspaceApp.jsx';
import { surfaceFor } from './lib/route.js';

export default function App() {
  const surface = surfaceFor(window.location.pathname);
  if (surface === 'console') return <ConsoleApp />;
  return <WorkspaceApp compact={surface === 'assistant'} />;
}
