﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.R.Core.AST;
using Microsoft.R.Editor.ContentType;
using Microsoft.R.Editor.Document;
using Microsoft.R.Editor.SuggestedActions.Actions;
using Microsoft.R.Editor.SuggestedActions.Definitions;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace Microsoft.R.Editor.SuggestedActions.Providers {
    [Export(typeof(IRSuggestedActionProvider))]
    [ContentType(RContentTypeDefinition.ContentType)]
    [Name("R Library Statement Suggested Action Provider")]
    internal sealed class LibraryStatementSuggestedActionProvider : IRSuggestedActionProvider {
        private static readonly Guid _treeUsedId = new Guid("{F0C102DF-9B3E-4C69-9CFE-23C244DBC7C4}");
        public IEnumerable<ISuggestedAction> GetSuggestedActions(ITextView textView, ITextBuffer textBuffer, int caretPosition) {
            return new ISuggestedAction[] {
                new InstallPackageSuggestedAction(textView, textBuffer, caretPosition),
                new LoadLibrarySuggestedAction(textView, textBuffer, caretPosition)
            };
        }

        public bool HasSuggestedActions(ITextView textView, ITextBuffer textBuffer, int caretPosition) {
            string libraryName = null;
            var doc = REditorDocument.TryFromTextBuffer(textBuffer);
            if(doc != null && doc.EditorTree.IsReady) {
                var ast = doc.EditorTree.AcquireReadLock(_treeUsedId);
                try {
                    libraryName = ast.IsInLibraryStatement(caretPosition);
                }
                finally {
                    doc.EditorTree.ReleaseReadLock(_treeUsedId);
                }
            }
            return !string.IsNullOrEmpty(libraryName);
        }
    }
}
