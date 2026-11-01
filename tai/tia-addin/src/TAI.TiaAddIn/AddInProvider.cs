using System.Collections.Generic;
using Siemens.Engineering;
using Siemens.Engineering.AddIn;
using Siemens.Engineering.AddIn.Menu;

namespace TAI.TiaAddIn
{
    /// <summary>
    /// Entry point discovered by TIA Portal: contributes the "TAI 工程助手" context menu
    /// to the project tree. TIA Portal constructs the provider with the hosting
    /// <see cref="TiaPortal"/> instance.
    /// </summary>
    public sealed class AddInProvider : ProjectTreeAddInProvider
    {
        private readonly TiaPortal _tiaPortal;

        public AddInProvider(TiaPortal tiaPortal)
        {
            _tiaPortal = tiaPortal;
        }

        protected override IEnumerable<ContextMenuAddIn> GetContextMenuAddIns()
        {
            yield return new AssistantContextMenuAddIn(_tiaPortal);
        }
    }
}
