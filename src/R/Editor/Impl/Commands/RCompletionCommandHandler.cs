﻿using Microsoft.Languages.Editor.Completion;
using Microsoft.Languages.Editor.Services;
using Microsoft.Languages.Editor.Shell;
using Microsoft.R.Editor.Completion;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace Microsoft.R.Editor.Commands
{
    internal class RCompletionCommandHandler : CompletionCommandHandler
    {
        ITextBuffer _textBuffer;
        ICompletionBroker _completionBroker;

        public RCompletionCommandHandler(ITextView textView, ITextBuffer textBuffer)
            : base(textView)
        {
            _textBuffer = textBuffer;
        }

        public override CompletionController CompletionController
        {
            get { return ServiceManager.GetService<RCompletionController>(TextView); }
        }

        private ICompletionBroker CompletionBroker
        {
            get
            {
                if (_completionBroker == null)
                {
                    _completionBroker = EditorShell.Current.ExportProvider.GetExport<ICompletionBroker>().Value;
                }

                return _completionBroker;
            }
        }
    }
}
