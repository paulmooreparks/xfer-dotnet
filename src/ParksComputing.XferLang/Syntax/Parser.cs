using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ParksComputing.XferLang.Syntax;

/// <summary>
/// Turns the token stream into the lossless tree (specification 5 and 7), reporting every fault
/// as a diagnostic and recovering so that one document yields each fault the parser can separate.
/// </summary>
/// <remarks>
/// A port of xfer-js <c>src/parser.ts</c>, with its recorded departures from its own design
/// (stray separators skipped, a wrong closer closing the collection it was meant for, the overflow
/// frame past the depth limit, constant-time wrong-closer matching, annotation completeness before
/// validation). An explicit stack of frames replaces recursion, so nesting depth is bounded by the
/// depth limit and by memory, never by the call stack.
/// </remarks>
internal sealed class Parser
{
    private static readonly XferAnnotationNode[] None = [];
    private static readonly int[] NoTokens = [];
    private static readonly HashSet<string> KnownAnnotations = new(StringComparer.Ordinal) { "xfer", "id", "tag", "lang" };

    private readonly SyntaxSource source;
    private readonly List<Token> tokens;
    private readonly XferLimits limits;
    private readonly List<PendingDiagnostic> diagnostics;
    private readonly List<Frame> stack = [];

    private int next;
    private XferNode? root;
    private bool rootDone;
    private XferAnnotationNode? head;
    private int version = 1;
    private readonly List<int> trailing = [];
    private bool trailingReported;
    private int depth;
    private bool depthReported;
    private int annotationDepth;

    // Objects and sequences open since the nearest enclosing annotation (or the start), so that a
    // wrong closing bracket can be matched against an outer collection in constant time.
    private int openObjects;
    private int openSequences;
    private long elements;
    private bool elementsReported;
    private readonly HashSet<string> ids = new(StringComparer.Ordinal);
    private int endOfFile = -1;
    private bool done;

    private Parser(SyntaxSource source, XferLimits limits, List<PendingDiagnostic> diagnostics)
    {
        this.source = source;
        tokens = source.Stream.Tokens;
        this.limits = limits;
        this.diagnostics = diagnostics;
        stack.Add(new DocumentFrame());
    }

    /// <summary>Parses a whole token stream. The stream must end with the end-of-file token.</summary>
    public static (XferSyntaxTree Tree, int Version) Parse(SyntaxSource source, XferLimits limits, List<PendingDiagnostic> diagnostics)
    {
        var parser = new Parser(source, limits, diagnostics);
        parser.Run();
        var tree = new XferSyntaxTree(source, parser.head, parser.root, [.. parser.trailing], parser.endOfFile);
        return (tree, parser.version);
    }

    // Frames: the document, each open container, and each open annotation.

    private abstract class Frame
    {
        public List<XferAnnotationNode> Pending { get; set; } = [];
    }

    private sealed class DocumentFrame : Frame
    {
    }

    private sealed class SequenceFrame : Frame
    {
        public int Open { get; init; }

        public XferAnnotationNode[] Annotations { get; init; } = None;

        public List<XferNode> Items { get; } = [];
    }

    private sealed class ObjectFrame : Frame
    {
        public int Open { get; init; }

        public XferAnnotationNode[] Annotations { get; init; } = None;

        public List<XferPairNode> Pairs { get; } = [];

        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);

        // Between a key and its value: the key (-1 when a bracket stood where a key belongs), the
        // annotations that came before it, and any stray separators skipped since.
        public bool ExpectingValue { get; set; }

        public int Key { get; set; } = -1;

        public XferAnnotationNode[] PairAnnotations { get; set; } = None;

        public List<XferSkippedSyntax> Skipped { get; set; } = [];
    }

    private enum AnnotationPhase
    {
        Name,
        Value,
        Close,
    }

    private sealed class AnnotationFrame : Frame
    {
        public int Open { get; init; }

        public int Name { get; set; } = -1;

        public XferNode? Value { get; set; }

        public int Close { get; set; } = -1;

        public AnnotationPhase Phase { get; set; } = AnnotationPhase.Name;

        // The very first token of the document, with no comment before it: the only place the
        // xfer head may stand.
        public bool CandidateHead { get; init; }

        // How many objects and sequences were open outside this annotation, saved so that a
        // closing bracket inside the annotation's value is never matched with one outside it.
        public int OuterObjects { get; init; }

        public int OuterSequences { get; init; }
    }

    /// <summary>
    /// A container deeper than the depth limit. It is not parsed: its tokens are kept in one error
    /// node and only its brackets are counted, so the contents add no diagnostics beyond the one
    /// for the limit and the tree stays lossless.
    /// </summary>
    private sealed class OverflowFrame : Frame
    {
        public XferAnnotationNode[] Annotations { get; init; } = None;

        public List<int> Tokens { get; } = [];

        public int Level { get; set; }
    }

    /// <summary>Hands over the annotations waiting for a target, leaving none behind.</summary>
    private static XferAnnotationNode[] Take(Frame frame)
    {
        if (frame.Pending.Count == 0)
        {
            return None;
        }

        XferAnnotationNode[] taken = [.. frame.Pending];
        frame.Pending = [];
        return taken;
    }

    private XferErrorNode ErrorNode(int[] tokenIndices, XferAnnotationNode[] annotations) => new(source, annotations, tokenIndices);

    private static bool IsScalarKind(XferTokenKind kind) => kind is XferTokenKind.String or XferTokenKind.Integer or XferTokenKind.Fractional
        or XferTokenKind.Date or XferTokenKind.Time or XferTokenKind.LocalDateTime or XferTokenKind.DateTime
        or XferTokenKind.Keyword or XferTokenKind.Identifier;

    /// <summary>An annotation with a name, a value that is a value and a closing <c>!&gt;</c>: one with a meaning to check.</summary>
    private bool IsComplete(XferAnnotationNode a)
    {
        return a.NameIndex >= 0 && tokens[a.NameIndex].Kind == XferTokenKind.Identifier
            && a.Value is not null && a.Value.Kind != XferNodeKind.Error
            && a.CloseIndex >= 0;
    }

    /// <summary>
    /// A <c>,</c> or <c>:</c> carried over from JSON or YAML. The lexer has reported it, with a
    /// hint. The parser skips it instead of reading it as a key or a value, because reading it as
    /// one shifts every key and value after it.
    /// </summary>
    private bool IsStraySeparator(int index)
    {
        Token t = tokens[index];
        return t.Kind == XferTokenKind.Error && t.Length == 1 && source.Bytes[t.Start] is (byte)',' or (byte)':';
    }

    private string KeyText(int index) => tokens[index].Kind == XferTokenKind.String ? source.ContentOf(index) : source.TextOf(index);

    private string Describe(XferNode? node)
    {
        if (node is null || node.Kind == XferNodeKind.Error)
        {
            return "?";
        }

        if (node is XferObjectNode)
        {
            return "object";
        }

        if (node is XferSequenceNode)
        {
            return "sequence";
        }

        var scalar = (XferScalarNode)node;
        XferTokenKind kind = tokens[scalar.TokenIndex].Kind;
        return $"{(kind == XferTokenKind.Identifier ? "symbol" : JsKindName(kind))} {Messages.Shorten(source.TextOf(scalar.TokenIndex))}";
    }

    /// <summary>The token kind as xfer-js names it, so the messages match.</summary>
    private static string JsKindName(XferTokenKind kind) => kind switch
    {
        XferTokenKind.String => "string",
        XferTokenKind.Integer => "integer",
        XferTokenKind.Fractional => "fractional",
        XferTokenKind.Date => "date",
        XferTokenKind.Time => "time",
        XferTokenKind.LocalDateTime => "local-datetime",
        XferTokenKind.DateTime => "datetime",
        XferTokenKind.Keyword => "keyword",
        XferTokenKind.Identifier => "identifier",
        _ => kind.ToString(),
    };

    private void Run()
    {
        int token = Advance();
        while (!done)
        {
            // A step consumes the token, or closes a frame and looks at the same token again. Every
            // step that does not consume pops a frame or ends the document, so this terminates.
            if (Dispatch(token) && !done)
            {
                token = Advance();
            }
        }
    }

    // Reading and reporting.

    private int Advance() => next < tokens.Count ? next++ : tokens.Count - 1;

    private Frame Top => stack[^1];

    private void Report(XferDiagnosticCode code, int at, string? detail = null)
    {
        Token t = tokens[at];
        diagnostics.Add(new PendingDiagnostic(code, t.Start, detail, t.End));
    }

    private void Count(int at)
    {
        elements++;
        if (elements > limits.Elements && !elementsReported)
        {
            elementsReported = true;
            Report(XferDiagnosticCode.XFER0605, at);
        }
    }

    private bool Dispatch(int token)
    {
        return Top switch
        {
            DocumentFrame frame => OnDocument(frame, token),
            SequenceFrame frame => OnSequence(frame, token),
            ObjectFrame frame => frame.ExpectingValue ? OnObjectValue(frame, token) : OnObject(frame, token),
            AnnotationFrame frame => OnAnnotation(frame, token),
            OverflowFrame frame => OnOverflow(frame, token),
            _ => throw new InvalidOperationException("unknown frame"),
        };
    }

    // Values.

    /// <summary>Handles a token that can begin a value. Never refuses a token other than end of file.</summary>
    private bool StartValue(int token, XferAnnotationNode[] annotations)
    {
        XferTokenKind kind = tokens[token].Kind;
        switch (kind)
        {
            case XferTokenKind.LeftBrace:
            case XferTokenKind.LeftBracket:
                OpenContainer(token, annotations);
                return true;
            case XferTokenKind.AnnotationClose:
                Report(XferDiagnosticCode.XFER0103, token, "`!>` closes an annotation, and none is open");
                Count(token);
                Deliver(ErrorNode([token], annotations));
                return true;
            case XferTokenKind.EndOfFile:
                return false;
            default:
                // Scalars, and the error tokens the lexer already reported.
                Count(token);
                Deliver(IsScalarKind(kind) ? new XferScalarNode(source, annotations, token) : ErrorNode([token], annotations));
                return true;
        }
    }

    private void OpenContainer(int token, XferAnnotationNode[] annotations)
    {
        Count(token);
        if (depth >= limits.Depth)
        {
            if (!depthReported)
            {
                depthReported = true;
                Report(XferDiagnosticCode.XFER0602, token, $"more than {limits.Depth} levels");
            }

            var overflow = new OverflowFrame { Annotations = annotations, Level = 1 };
            overflow.Tokens.Add(token);
            stack.Add(overflow);
            return;
        }

        depth++;
        if (tokens[token].Kind == XferTokenKind.LeftBracket)
        {
            openSequences++;
            stack.Add(new SequenceFrame { Open = token, Annotations = annotations });
        }
        else
        {
            openObjects++;
            stack.Add(new ObjectFrame { Open = token, Annotations = annotations });
        }
    }

    /// <summary>Closes the innermost container. A missing closing bracket is reported by the caller.</summary>
    private void CloseContainer(Frame frame, int close)
    {
        if (frame.Pending.Count > 0)
        {
            // A malformed annotation has been reported already; only a whole one is also "dangling".
            XferAnnotationNode? dangling = frame.Pending.FirstOrDefault(IsComplete);
            if (dangling is not null)
            {
                Report(XferDiagnosticCode.XFER0502, dangling.OpenIndex, "it is the last thing in its collection");
            }

            if (frame is SequenceFrame s)
            {
                s.Items.Add(ErrorNode(NoTokens, Take(frame)));
            }
            else
            {
                ((ObjectFrame)frame).Pairs.Add(new XferPairNode(source, Take(frame), -1, null, null));
            }
        }

        stack.RemoveAt(stack.Count - 1);
        depth--;
        if (frame is SequenceFrame sequence)
        {
            openSequences--;
            Deliver(new XferSequenceNode(source, sequence.Annotations, sequence.Open, close, [.. sequence.Items]));
        }
        else
        {
            var obj = (ObjectFrame)frame;
            openObjects--;
            Deliver(new XferObjectNode(source, obj.Annotations, obj.Open, close, [.. obj.Pairs]));
        }
    }

    /// <summary>A finished value goes to the frame that was waiting for it.</summary>
    private void Deliver(XferNode node)
    {
        switch (Top)
        {
            case DocumentFrame:
                root = node;
                rootDone = true;
                break;
            case SequenceFrame s:
                s.Items.Add(node);
                break;
            case ObjectFrame o:
                o.Pairs.Add(new XferPairNode(source, o.PairAnnotations, o.Key, node, o.Skipped.Count == 0 ? null : [.. o.Skipped]));
                o.Key = -1;
                o.PairAnnotations = None;
                o.Skipped.Clear();
                o.ExpectingValue = false;
                break;
            case AnnotationFrame a:
                a.Value = node;
                a.Phase = AnnotationPhase.Close;
                break;
            case OverflowFrame:
                break; // an overflowing container takes its tokens directly and never waits for a value
        }
    }

    /// <summary>A container past the depth limit: keep its tokens, count its brackets, and say nothing more.</summary>
    private bool OnOverflow(OverflowFrame frame, int token)
    {
        XferTokenKind kind = tokens[token].Kind;
        if (kind == XferTokenKind.EndOfFile)
        {
            Report(XferDiagnosticCode.XFER0405, frame.Tokens[0], "the document ends inside it");
            stack.RemoveAt(stack.Count - 1);
            Deliver(ErrorNode([.. frame.Tokens], frame.Annotations));
            return false;
        }

        frame.Tokens.Add(token);
        if (kind is XferTokenKind.LeftBrace or XferTokenKind.LeftBracket)
        {
            frame.Level++;
        }
        else if (kind is XferTokenKind.RightBrace or XferTokenKind.RightBracket)
        {
            frame.Level--;
            if (frame.Level == 0)
            {
                stack.RemoveAt(stack.Count - 1);
                Deliver(ErrorNode([.. frame.Tokens], frame.Annotations));
            }
        }

        return true;
    }

    // The document.

    private bool OnDocument(DocumentFrame frame, int token)
    {
        XferTokenKind kind = tokens[token].Kind;
        if (rootDone)
        {
            if (kind == XferTokenKind.EndOfFile)
            {
                endOfFile = token;
                done = true;
                return false;
            }

            // Everything after the root is kept, unparsed, so the tree stays lossless. A token the
            // lexer already reported as malformed is not also reported as content after the root.
            if (kind != XferTokenKind.Error && !trailingReported)
            {
                trailingReported = true;
                Report(XferDiagnosticCode.XFER0402, token);
            }

            trailing.Add(token);
            return true;
        }

        switch (kind)
        {
            case XferTokenKind.AnnotationOpen:
                OpenAnnotation(token);
                return true;
            case XferTokenKind.RightBrace:
            case XferTokenKind.RightBracket:
                Report(XferDiagnosticCode.XFER0406, token, "there is nothing open to close");
                Count(token);
                Deliver(ErrorNode([token], Take(frame)));
                return true;
            case XferTokenKind.EndOfFile:
                endOfFile = token;
                done = true;
                if (frame.Pending.Count > 0)
                {
                    // A malformed annotation has been reported already; only a whole one is also dangling.
                    XferAnnotationNode? dangling = frame.Pending.FirstOrDefault(IsComplete);
                    if (dangling is not null)
                    {
                        Report(XferDiagnosticCode.XFER0502, dangling.OpenIndex, "the document ends after it");
                    }

                    root = ErrorNode(NoTokens, Take(frame));
                }

                Report(XferDiagnosticCode.XFER0401, token);
                return false;
            default:
                return StartValue(token, Take(frame));
        }
    }

    // Sequences.

    private bool OnSequence(SequenceFrame frame, int token)
    {
        switch (tokens[token].Kind)
        {
            case XferTokenKind.AnnotationOpen:
                OpenAnnotation(token);
                return true;
            case XferTokenKind.RightBracket:
                CloseContainer(frame, token);
                return true;
            case XferTokenKind.RightBrace:
                return WrongCloser(frame, token);
            case XferTokenKind.EndOfFile:
                Report(XferDiagnosticCode.XFER0405, frame.Open, "expected `]`");
                CloseContainer(frame, -1);
                return false;
            default:
                return StartValue(token, Take(frame));
        }
    }

    /// <summary>
    /// A closing bracket of the other kind. If a collection of that kind is open further out, the
    /// bracket belongs to it, so this one is unterminated and is closed without the bracket, which
    /// the next frame then reads. Otherwise the bracket is a mistake for this collection's own, and
    /// closes it, so one slip is one error.
    /// </summary>
    private bool WrongCloser(Frame frame, int token)
    {
        string wanted = frame is SequenceFrame ? "]" : "}";
        int open = frame is SequenceFrame s ? s.Open : ((ObjectFrame)frame).Open;

        // This frame is the other kind from the bracket, so the counter holds only outer collections.
        int outer = tokens[token].Kind == XferTokenKind.RightBrace ? openObjects : openSequences;
        if (outer > 0)
        {
            Report(XferDiagnosticCode.XFER0405, open, $"expected `{wanted}`");
            CloseContainer(frame, -1);
            return false;
        }

        Report(XferDiagnosticCode.XFER0406, token, $"expected `{wanted}`");
        CloseContainer(frame, token);
        return true;
    }

    // Objects.

    private bool OnObject(ObjectFrame frame, int token)
    {
        XferTokenKind kind = tokens[token].Kind;
        switch (kind)
        {
            case XferTokenKind.AnnotationOpen:
                OpenAnnotation(token);
                return true;
            case XferTokenKind.RightBrace:
                CloseContainer(frame, token);
                return true;
            case XferTokenKind.RightBracket:
                return WrongCloser(frame, token);
            case XferTokenKind.EndOfFile:
                Report(XferDiagnosticCode.XFER0405, frame.Open, "expected `}`");
                CloseContainer(frame, -1);
                return false;
            case XferTokenKind.AnnotationClose:
                // Skipped, not read as a key: this is what is left when "<!" was forgotten, as in
                // { tag x !> a 1 }, and reading it as a key would swallow the next key as its value.
                Report(XferDiagnosticCode.XFER0103, token, "`!>` closes an annotation, and none is open");
                frame.Pairs.Add(new XferPairNode(source, Take(frame), token, null, null));
                return true;
            case XferTokenKind.LeftBrace:
            case XferTokenKind.LeftBracket:
                // A container where a key belongs. It is read as a value so its contents do not cascade.
                Report(XferDiagnosticCode.XFER0407, token, "a key is an identifier or a string");
                frame.Key = -1;
                frame.PairAnnotations = Take(frame);
                frame.ExpectingValue = true;
                return StartValue(token, None);
            case XferTokenKind.Identifier:
            case XferTokenKind.String:
                TakeKey(frame, token);
                return true;
            case XferTokenKind.Error:
                if (IsStraySeparator(token))
                {
                    frame.Pairs.Add(new XferPairNode(source, Take(frame), token, null, null));
                    return true;
                }

                // The lexer reported it already; keep it as the key so its value is consumed quietly.
                frame.Key = token;
                frame.PairAnnotations = Take(frame);
                frame.ExpectingValue = true;
                return true;
            default:
                Report(XferDiagnosticCode.XFER0407, token, $"found {(kind == XferTokenKind.Keyword ? "the keyword " : string.Empty)}{source.TextOf(token)}");
                frame.Key = token;
                frame.PairAnnotations = Take(frame);
                frame.ExpectingValue = true;
                return true;
        }
    }

    private void TakeKey(ObjectFrame frame, int token)
    {
        string text = KeyText(token);
        if (!frame.Keys.Add(text))
        {
            Report(XferDiagnosticCode.XFER0403, token, $"the key {Messages.JsQuote(Messages.Shorten(text))} is already used");
        }

        frame.Key = token;
        frame.PairAnnotations = Take(frame);
        frame.ExpectingValue = true;
    }

    private bool OnObjectValue(ObjectFrame frame, int token)
    {
        switch (tokens[token].Kind)
        {
            case XferTokenKind.AnnotationOpen:
                Report(XferDiagnosticCode.XFER0502, token, "an annotation goes before a key, not between a key and its value");
                OpenAnnotation(token);
                return true;
            case XferTokenKind.RightBrace:
                MissingValue(frame);
                CloseContainer(frame, token);
                return true;
            case XferTokenKind.EndOfFile:
                MissingValue(frame);
                Report(XferDiagnosticCode.XFER0405, frame.Open, "expected `}`");
                CloseContainer(frame, -1);
                return false;
            case XferTokenKind.RightBracket:
                MissingValue(frame);
                return WrongCloser(frame, token);
            case XferTokenKind.Error:
                if (IsStraySeparator(token))
                {
                    // "a": 1 has a stray colon after its key; skip it and read the value that follows.
                    frame.Skipped.Add(new XferSkippedSyntax(source, token));
                    return true;
                }

                return StartValue(token, Take(frame));
            default:
                return StartValue(token, Take(frame));
        }
    }

    /// <summary>A key with no value: reports it, lists how the parser paired what it read, and records the pair.</summary>
    private void MissingValue(ObjectFrame frame)
    {
        int key = frame.Key;
        if (key >= 0 && IsScalarKind(tokens[key].Kind))
        {
            var paired = new List<string>(frame.Pairs.Count + 1);
            foreach (XferPairNode p in frame.Pairs)
            {
                paired.Add($"{(p.KeyIndex < 0 ? "?" : Messages.Shorten(KeyText(p.KeyIndex)))}={Describe(p.Value)}");
            }

            paired.Add($"{Messages.Shorten(KeyText(key))}=?");
            IEnumerable<string> shown = paired.Count > 8 ? paired.Skip(paired.Count - 8).Prepend("...") : paired;
            Report(
                XferDiagnosticCode.XFER0404,
                key,
                $"a value is missing for {Messages.JsQuote(Messages.Shorten(KeyText(key)))}; the parser paired {string.Join(", ", shown)}");
        }

        // An annotation after the key goes to Skipped, so nothing is pending between a key and its value.
        frame.Pairs.Add(new XferPairNode(source, frame.PairAnnotations, key, null, frame.Skipped.Count == 0 ? null : [.. frame.Skipped]));
        frame.Key = -1;
        frame.PairAnnotations = None;
        frame.Skipped.Clear();
        frame.ExpectingValue = false;
    }

    // Annotations (specification 7).

    private void OpenAnnotation(int token)
    {
        if (annotationDepth > 0)
        {
            Report(XferDiagnosticCode.XFER0501, token, "an annotation's value cannot carry an annotation");
        }

        annotationDepth++;
        Token raw = tokens[token];
        bool noComment = true;
        for (int i = raw.FirstTrivia; i < raw.FirstTrivia + raw.TriviaCount; i++)
        {
            if (source.Stream.Trivia[i].Kind == XferTriviaKind.Comment)
            {
                noComment = false;
            }
        }

        stack.Add(new AnnotationFrame
        {
            Open = token,
            CandidateHead = token == 0 && noComment,
            OuterObjects = openObjects,
            OuterSequences = openSequences,
        });

        // Brackets inside the annotation's value are matched only with each other.
        openObjects = 0;
        openSequences = 0;
    }

    private void ReportMalformed(AnnotationFrame frame, string what) => Report(XferDiagnosticCode.XFER0506, frame.Open, $"the annotation {what}");

    private bool OnAnnotation(AnnotationFrame frame, int token)
    {
        XferTokenKind kind = tokens[token].Kind;
        switch (frame.Phase)
        {
            case AnnotationPhase.Name:
                // A token the lexer reported is kept as the name, so one fault is one error; an
                // annotation with such a name is not checked further (IsComplete).
                if (kind is XferTokenKind.Identifier or XferTokenKind.Error)
                {
                    frame.Name = token;
                    frame.Phase = AnnotationPhase.Value;
                    return true;
                }

                ReportMalformed(frame, "has no name");
                if (kind == XferTokenKind.AnnotationClose)
                {
                    frame.Close = token;
                    FinishAnnotation(frame);
                    return true;
                }

                // Leave the token for the enclosing frame; it is not part of this annotation.
                FinishAnnotation(frame);
                return false;

            case AnnotationPhase.Value:
                switch (kind)
                {
                    case XferTokenKind.AnnotationOpen:
                        OpenAnnotation(token);
                        return true;
                    case XferTokenKind.AnnotationClose:
                        ReportMalformed(frame, "has no value");
                        frame.Value = frame.Pending.Count > 0 ? ErrorNode(NoTokens, Take(frame)) : null;
                        frame.Close = token;
                        FinishAnnotation(frame);
                        return true;
                    case XferTokenKind.RightBrace:
                    case XferTokenKind.RightBracket:
                    case XferTokenKind.EndOfFile:
                        ReportMalformed(frame, "has no value");
                        frame.Value = frame.Pending.Count > 0 ? ErrorNode(NoTokens, Take(frame)) : null;
                        FinishAnnotation(frame);
                        return false;
                    default:
                        return StartValue(token, Take(frame));
                }

            default:
                if (kind == XferTokenKind.AnnotationClose)
                {
                    frame.Close = token;
                    FinishAnnotation(frame);
                    return true;
                }

                // An annotation takes one value. If the name is not one the specification defines,
                // say that first: <! let x 1 !> is a legacy form, and "unknown annotation" is the cause.
                if (frame.Name >= 0 && !KnownAnnotations.Contains(source.TextOf(frame.Name)))
                {
                    Report(XferDiagnosticCode.XFER0506, frame.Open, $"unknown annotation {Messages.JsQuote(Messages.Shorten(source.TextOf(frame.Name)))}");
                }
                else
                {
                    ReportMalformed(frame, "is not closed: expected `!>`");
                }

                FinishAnnotation(frame);
                return false;
        }
    }

    private void FinishAnnotation(AnnotationFrame frame)
    {
        stack.RemoveAt(stack.Count - 1);
        annotationDepth--;
        openObjects = frame.OuterObjects;
        openSequences = frame.OuterSequences;
        var node = new XferAnnotationNode(source, frame.Open, frame.Name, frame.Value, frame.Close);

        // Only a complete annotation has a meaning to check; an incomplete one is already reported.
        bool complete = IsComplete(node);
        if (complete && IsHead(frame, node))
        {
            return;
        }

        if (complete)
        {
            Validate(frame, node);
        }

        Frame lower = Top;
        if (lower is ObjectFrame { ExpectingValue: true } obj)
        {
            // After a key and before its value is not a place for an annotation (it was reported
            // when it opened). It is kept, in order, with whatever else was skipped there.
            obj.Skipped.Add(new XferSkippedSyntax(node));
        }
        else if (lower is not OverflowFrame)
        {
            lower.Pending.Add(node);
        }
    }

    /// <summary>The xfer head: first token of the document, nothing before it but whitespace.</summary>
    private bool IsHead(AnnotationFrame frame, XferAnnotationNode node)
    {
        if (!frame.CandidateHead || source.TextOf(node.NameIndex) != "xfer")
        {
            return false;
        }

        head = node;
        int token = node.Value is XferScalarNode scalar ? scalar.TokenIndex : -1;
        string text = token >= 0 ? source.TextOf(token) : string.Empty;
        if (token < 0 || tokens[token].Kind != XferTokenKind.Integer || !IsPlainWholeNumber(text))
        {
            Report(XferDiagnosticCode.XFER0504, frame.Open, "the version is a plain whole number, as in <! xfer 1 !>");
        }
        else
        {
            // A version too large for an int is still not 1, and is reported as unsupported.
            version = int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? v : int.MaxValue;
            if (text != "1")
            {
                Report(XferDiagnosticCode.XFER0504, frame.Open, $"version {text} is not supported; this parser reads version 1");
            }
        }

        return true;
    }

    /// <summary>The pattern <c>^(0|[1-9][0-9]*)$</c>.</summary>
    private static bool IsPlainWholeNumber(string text)
    {
        if (text.Length == 0 || (text[0] == '0' && text.Length > 1))
        {
            return false;
        }

        foreach (char c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private void Validate(AnnotationFrame frame, XferAnnotationNode node)
    {
        string name = source.TextOf(node.NameIndex);
        int token = node.Value is XferScalarNode scalar ? scalar.TokenIndex : -1;
        XferTokenKind? kind = token >= 0 ? tokens[token].Kind : null;
        void WrongType(string wants) => Report(XferDiagnosticCode.XFER0506, frame.Open, $"`{name}` takes {wants}");

        switch (name)
        {
            case "xfer":
                Report(XferDiagnosticCode.XFER0503, frame.Open, "it must be the first token of the document");
                break;
            case "id":
                if (kind != XferTokenKind.String)
                {
                    WrongType("a string");
                    break;
                }

                string id = source.ContentOf(token);
                if (!ids.Add(id))
                {
                    Report(XferDiagnosticCode.XFER0505, frame.Open, $"the id {Messages.JsQuote(Messages.Shorten(id))} is already used");
                }

                break;
            case "tag":
            case "lang":
                if (kind is not (XferTokenKind.String or XferTokenKind.Identifier))
                {
                    WrongType("a string or a symbol");
                }

                break;
            default:
                Report(XferDiagnosticCode.XFER0506, frame.Open, $"unknown annotation {Messages.JsQuote(Messages.Shorten(name))}");
                break;
        }
    }
}

/// <summary>Message helpers that write text as xfer-js writes it, so the two implementations' messages match.</summary>
internal static class Messages
{
    /// <summary>
    /// xfer-js <c>shorten</c>: more than 24 UTF-16 units becomes the first 21 and "...", or the
    /// first 20 when the 21st would be the first half of a surrogate pair.
    /// </summary>
    public static string Shorten(string text)
    {
        if (text.Length <= 24)
        {
            return text;
        }

        int cut = char.IsHighSurrogate(text[20]) ? 20 : 21;
        return text[..cut] + "...";
    }

    /// <summary>A string as JavaScript's <c>JSON.stringify</c> writes it, lone surrogates included.</summary>
    public static string JsQuote(string text)
    {
        var sb = new StringBuilder(text.Length + 2);
        sb.Append('"');
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        sb.Append(c).Append(text[++i]);
                    }
                    else if (char.IsSurrogate(c))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append('"').ToString();
    }
}
