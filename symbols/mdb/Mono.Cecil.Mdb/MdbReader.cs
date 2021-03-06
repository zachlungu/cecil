//
// Author:
//   Jb Evain (jbevain@gmail.com)
//
// Copyright (c) 2008 - 2015 Jb Evain
// Copyright (c) 2008 - 2011 Novell, Inc.
//
// Licensed under the MIT/X11 license.
//

using System;
using System.Collections.Generic;
using System.IO;

using Mono.Cecil.Cil;
using Mono.Collections.Generic;
using Mono.CompilerServices.SymbolWriter;

namespace Mono.Cecil.Mdb {

	public class MdbReaderProvider : ISymbolReaderProvider {

		public ISymbolReader GetSymbolReader (ModuleDefinition module, string fileName)
		{
			return new MdbReader (module, MonoSymbolFile.ReadSymbolFile (fileName + ".mdb", module.Mvid));
		}

		public ISymbolReader GetSymbolReader (ModuleDefinition module, Stream symbolStream)
		{
			var file = MonoSymbolFile.ReadSymbolFile (symbolStream);
			if (module.Mvid != file.Guid) {
				var file_stream = symbolStream as FileStream;
				if (file_stream != null)
					throw new MonoSymbolFileException ("Symbol file `{0}' does not match assembly", file_stream.Name);

				throw new MonoSymbolFileException ("Symbol file from stream does not match assembly");
			}
			return new MdbReader (module, file);
		}
	}

	public class MdbReader : ISymbolReader {

		readonly ModuleDefinition module;
		readonly MonoSymbolFile symbol_file;
		readonly Dictionary<string, Document> documents;

		public MdbReader (ModuleDefinition module, MonoSymbolFile symFile)
		{
			this.module = module;
			this.symbol_file = symFile;
			this.documents = new Dictionary<string, Document> ();
		}

		public bool ProcessDebugHeader (ImageDebugDirectory directory, byte [] header)
		{
			return symbol_file.Guid == module.Mvid;
		}

		public MethodDebugInformation Read (MethodDefinition method)
		{
			var method_token = method.MetadataToken;
			var entry = symbol_file.GetMethodByToken (method_token.ToInt32	());
			if (entry == null)
				return null;

			var info = new MethodDebugInformation (method);

			var scopes = ReadScopes (entry, info);
			ReadLineNumbers (entry, info);
			ReadLocalVariables (entry, scopes);

			return info;
		}

		static void ReadLocalVariables (MethodEntry entry, ScopeDebugInformation [] scopes)
		{
			var locals = entry.GetLocals ();

			foreach (var local in locals) {
				var variable = new VariableDebugInformation (local.Index, local.Name);

				var index = local.BlockIndex - 1;
				if (index < 0 || index >= scopes.Length)
					continue;

				var scope = scopes [index];
				if (scope == null)
					continue;

				scope.Variables.Add (variable);
			}
		}

		void ReadLineNumbers (MethodEntry entry, MethodDebugInformation info)
		{
			Document document = null;
			var table = entry.GetLineNumberTable ();

			info.sequence_points = new Collection<SequencePoint> (table.LineNumbers.Length);

			for (var i = 0; i < table.LineNumbers.Length; i++) {
				var line = table.LineNumbers [i];
				if (document == null)
					document = GetDocument (entry.CompileUnit.SourceFile);

				if (i > 0 && table.LineNumbers [i - 1].Offset == line.Offset)
					continue;

				info.sequence_points.Add (LineToSequencePoint (line, entry, document));
			}
		}

		Document GetDocument (SourceFileEntry file)
		{
			var file_name = file.FileName;

			Document document;
			if (documents.TryGetValue (file_name, out document))
				return document;

			document = new Document (file_name);
			documents.Add (file_name, document);

			return document;
		}

		static ScopeDebugInformation [] ReadScopes (MethodEntry entry, MethodDebugInformation info)
		{
			var blocks = entry.GetCodeBlocks ();
			var scopes = new ScopeDebugInformation [blocks.Length];

			foreach (var block in blocks) {
				if (block.BlockType != CodeBlockEntry.Type.Lexical)
					continue;

				var scope = new ScopeDebugInformation ();
				scope.Start = new InstructionOffset (block.StartOffset);
				scope.End = new InstructionOffset (block.EndOffset);

				scopes [block.Index] = scope;

				if (info.scope == null) {
					info.scope = scope;
					continue;
				}

				if (!AddScope (info.scope.Scopes, scope))
					info.scope.Scopes.Add (scope);
			}

			return scopes;
		}

		static bool AddScope (Collection<ScopeDebugInformation> scopes, ScopeDebugInformation scope)
		{
			foreach (var sub_scope in scopes) {
				if (sub_scope.HasScopes && AddScope (sub_scope.Scopes, scope))
					return true;

				if (scope.Start.Offset >= sub_scope.Start.Offset && scope.End.Offset <= sub_scope.End.Offset) {
					sub_scope.Scopes.Add (scope);
					return true;
				}
			}

			return false;
		}

		static SequencePoint LineToSequencePoint (LineNumberEntry line, MethodEntry entry, Document document)
		{
			return new SequencePoint (line.Offset, document) {
				StartLine = line.Row,
				EndLine = line.EndRow,
				StartColumn = line.Column,
				EndColumn = line.EndColumn,
			};
		}

		public void Dispose ()
		{
			symbol_file.Dispose ();
		}
	}

	static class MethodEntryExtensions {

		public static bool HasColumnInfo (this MethodEntry entry)
		{
			return (entry.MethodFlags & MethodEntry.Flags.ColumnsInfoIncluded) != 0;
		}

		public static bool HasEndInfo (this MethodEntry entry)
		{
			return (entry.MethodFlags & MethodEntry.Flags.EndInfoIncluded) != 0;
		}
	}
}
