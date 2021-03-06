﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;
using Cci = Microsoft.Cci;

namespace Microsoft.CodeAnalysis.CodeGen
{
    /// <summary>
    /// Maintains a list of sequence points in a space efficient way. Most of the time sequence points
    /// occur in the same syntax tree, so optimize for that case. Store a sequence point as an offset, and 
    /// position in a syntax tree, then translate to CCI format only on demand.
    /// 
    /// Use a ArrayBuilder{RawSequencePoint} to create.
    /// </summary>
    internal class SequencePointList
    {
        internal const int HiddenSequencePointLine = 0xFEEFEE;

        private readonly SyntaxTree tree;
        private readonly OffsetAndSpan[] points;
        private SequencePointList next;  // Linked list of all points.

        // No sequence points.
        private static readonly SequencePointList Empty = new SequencePointList();

        // Construct a list with no sequence points.
        private SequencePointList()
        {
            points = SpecializedCollections.EmptyArray<OffsetAndSpan>();
        }

        // Construct a list with sequence points from exactly one syntax tree.
        private SequencePointList(SyntaxTree tree, OffsetAndSpan[] points)
        {
            this.tree = tree;
            this.points = points;
        }

        /// <summary>
        /// Create a SequencePointList with the raw sequence points from an ArrayBuilder.
        /// A linked list of instances for each syntax tree is created (almost always of length one).
        /// </summary>
        public static SequencePointList Create(ArrayBuilder<RawSequencePoint> seqPointBuilder, ILBuilder builder)
        {
            if (seqPointBuilder.Count == 0)
            {
                return SequencePointList.Empty;
            }

            SequencePointList first = null, current = null;
            int totalPoints = seqPointBuilder.Count;
            int last = 0;

            for (int i = 1; i <= totalPoints; ++i)
            {
                if (i == totalPoints || seqPointBuilder[i].SyntaxTree != seqPointBuilder[i - 1].SyntaxTree)
                {
                    // Create a new list
                    SequencePointList next = new SequencePointList(seqPointBuilder[i - 1].SyntaxTree, GetSubArray(seqPointBuilder, last, i - last, builder));
                    last = i;

                    // Link together with any additional.
                    if (current == null)
                    {
                        first = current = next;
                    }
                    else
                    {
                        current.next = next;
                        current = next;
                    }
                }
            }

            return first;
        }

        public bool IsEmpty
        {
            get
            {
                return next == null && points.Length == 0;
            }
        }

        private static OffsetAndSpan[] GetSubArray(ArrayBuilder<RawSequencePoint> seqPointBuilder, int start, int length, ILBuilder builder)
        {
            OffsetAndSpan[] result = new OffsetAndSpan[length];
            for (int i = 0; i < result.Length; i++)
            {
                RawSequencePoint point = seqPointBuilder[i + start];
                int ilOffset = builder.GetILOffsetFromMarker(point.ILMarker);
                Debug.Assert(ilOffset >= 0);
                result[i] = new OffsetAndSpan(ilOffset, point.Span);
            }

            return result;
        }

        /// <summary>
        /// Get all the sequence points, possibly mapping them using #line/ExternalSource directives, and mapping
        /// file names to debug documents with the given mapping function.
        /// </summary>
        /// <param name="documentProvider">Function that maps file paths to CCI debug documents</param>
        public ImmutableArray<Cci.SequencePoint> GetSequencePoints(DebugDocumentProvider documentProvider)
        {
            bool lastPathIsMapped = false;
            string lastPath = null;
            Cci.DebugSourceDocument lastDebugDocument = null;

            // First, count the number of sequence points.
            int count = 0;
            SequencePointList current = this;
            while (current != null)
            {
                count += current.points.Length;
                current = current.next;
            }

            ArrayBuilder<Cci.SequencePoint> result = ArrayBuilder<Cci.SequencePoint>.GetInstance(count);
            current = this;
            while (current != null)
            {
                SyntaxTree currentTree = current.tree;

                foreach (var offsetAndSpan in current.points)
                {
                    TextSpan span = offsetAndSpan.Span;

                    // if it's a hidden sequence point, or a sequence point with syntax that points to a position that is inside 
                    // of a hidden region (can be defined with #line hidden (C#) or implicitly by #ExternalSource (VB), make it 
                    // a hidden sequence point.

                    bool isHidden = span == RawSequencePoint.HiddenSequencePointSpan;
                    FileLinePositionSpan fileLinePositionSpan = default(FileLinePositionSpan);
                    if (!isHidden)
                    {
                        fileLinePositionSpan = currentTree.GetMappedLineSpanAndVisibility(span, out isHidden);
                    }

                    if (isHidden)
                    {
                        if (lastPath == null)
                        {
                            lastPath = currentTree.FilePath;
                            lastDebugDocument = documentProvider(lastPath, basePath: null);
                        }

                        if (lastDebugDocument != null)
                        {
                            result.Add(new Cci.SequencePoint(
                                lastDebugDocument,
                                offset: offsetAndSpan.Offset,
                                startLine: HiddenSequencePointLine,
                                startColumn: 0,
                                endLine: HiddenSequencePointLine,
                                endColumn: 0));
                        }
                    }
                    else
                    {
                        if (lastPath != fileLinePositionSpan.Path || lastPathIsMapped != fileLinePositionSpan.HasMappedPath)
                        {
                            lastPath = fileLinePositionSpan.Path;
                            lastPathIsMapped = fileLinePositionSpan.HasMappedPath;
                            lastDebugDocument = documentProvider(lastPath, basePath: lastPathIsMapped ? currentTree.FilePath : null);
                        }

                        if (lastDebugDocument != null)
                        {
                            result.Add(new Cci.SequencePoint(
                                lastDebugDocument,
                                offset: offsetAndSpan.Offset,
                                startLine: (fileLinePositionSpan.StartLinePosition.Line == -1) ? 0 : fileLinePositionSpan.StartLinePosition.Line + 1,
                                startColumn: fileLinePositionSpan.StartLinePosition.Character + 1,
                                endLine: (fileLinePositionSpan.EndLinePosition.Line == -1) ? 0 : fileLinePositionSpan.EndLinePosition.Line + 1,
                                endColumn: fileLinePositionSpan.EndLinePosition.Character + 1
                            ));
                        }
                    }
                }

                current = current.next;
            }

            return result.ToImmutableAndFree();
        }

        /// <summary>
        /// Represents the combination of an IL offset and a source text span.
        /// </summary>
        private struct OffsetAndSpan
        {
            public readonly int Offset;
            public readonly TextSpan Span;

            public OffsetAndSpan(int offset, TextSpan span)
            {
                this.Offset = offset;
                this.Span = span;
            }
        }
    }
}
