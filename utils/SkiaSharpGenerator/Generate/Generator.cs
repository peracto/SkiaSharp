﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CppAst;

namespace SkiaSharpGenerator
{
	public class Generator : BaseTool
	{
		public Generator(string skiaRoot, string configFile, TextWriter outputWriter)
			: base(skiaRoot, configFile)
		{
			OutputWriter = outputWriter ?? throw new ArgumentNullException(nameof(outputWriter));
		}

		public TextWriter OutputWriter { get; }

		public async Task GenerateAsync()
		{
			Log?.Log("Starting C# API generation...");

			config = await LoadConfigAsync(ConfigFile);

			LoadStandardMappings();

			ParseSkiaHeaders();

			UpdatingMappings();

			WriteApi(OutputWriter);

			Log?.Log("C# API generation complete.");
		}

		private void WriteApi(TextWriter writer)
		{
			Log?.LogVerbose("Writing C# API...");

			writer.WriteLine("using System;");
			writer.WriteLine("using System.Runtime.InteropServices;");
			writer.WriteLine();
			writer.WriteLine($"namespace {config.Namespace}");
			writer.WriteLine($"{{");
			WriteClasses(writer);
			writer.WriteLine();
			writer.WriteLine($"\tinternal unsafe partial class {config.ClassName}");
			writer.WriteLine($"\t{{");
			WriteFunctions(writer);
			writer.WriteLine($"\t}}");
			writer.WriteLine();
			WriteDelegates(writer);
			writer.WriteLine();
			WriteStructs(writer);
			writer.WriteLine();
			WriteEnums(writer);
			writer.WriteLine($"}}");
		}

		private void WriteDelegates(TextWriter writer)
		{
			Log?.LogVerbose("  Writing delegates...");

			writer.WriteLine($"\t#region Delegates");

			var delegates = compilation.Typedefs
				.Where(t => t.ElementType.TypeKind == CppTypeKind.Pointer)
				.OrderBy(t => t.GetDisplayName());
			foreach (var del in delegates)
			{
				if (!(((CppPointerType)del.ElementType).ElementType is CppFunctionType function))
				{
					Log?.LogWarning($"Unknown delegate type {del}");

					writer.WriteLine($"// TODO: {del}");
					continue;
				}

				Log?.LogVerbose($"    {del.GetDisplayName()}");

				var name = del.GetDisplayName();
				functionMappings.TryGetValue(name, out var map);
				name = map?.CsType ?? Utils.CleanName(name);

				writer.WriteLine();
				writer.WriteLine($"\t// {del}");
				writer.WriteLine($"\t[UnmanagedFunctionPointer (CallingConvention.Cdecl)]");

				var paramsList = new List<string>();
				for (var i = 0; i < function.Parameters.Count; i++)
				{
					var p = function.Parameters[i];
					var n = string.IsNullOrEmpty(p.Name) ? $"param{i}" : p.Name;
					var t = GetType(p.Type);
					var cppT = GetCppType(p.Type);
					if (cppT == "bool")
						t = $"[MarshalAs (UnmanagedType.I1)] bool";
					if (map != null && map.Parameters.TryGetValue(i.ToString(), out var newT))
						t = newT;
					paramsList.Add($"{t} {n}");
				}

				var returnType = GetType(function.ReturnType);
				if (map != null && map.Parameters.TryGetValue("-1", out var newR))
				{
					returnType = newR;
				}
				else if (GetCppType(function.ReturnType) == "bool")
				{
					returnType = "bool";
					writer.WriteLine($"\t[return: MarshalAs (UnmanagedType.I1)]");
				}

				writer.WriteLine($"\tinternal unsafe delegate {returnType} {name}({string.Join(", ", paramsList)});");
			}

			writer.WriteLine();
			writer.WriteLine($"\t#endregion");
		}

		private void WriteStructs(TextWriter writer)
		{
			Log?.LogVerbose("  Writing structs...");

			writer.WriteLine($"\t#region Structs");

			var classes = compilation.Classes
				.Where(c => c.SizeOf != 0)
				.OrderBy(c => c.GetDisplayName())
				.ToList();
			foreach (var klass in classes)
			{
				Log?.LogVerbose($"    {klass.GetDisplayName()}");

				var name = klass.GetDisplayName();
				typeMappings.TryGetValue(name, out var map);
				name = map?.CsType ?? Utils.CleanName(name);

				writer.WriteLine();
				writer.WriteLine($"\t// {klass.GetDisplayName()}");
				writer.WriteLine($"\t[StructLayout (LayoutKind.Sequential)]");
				var visibility = map?.IsInternal == true ? "internal" : "public";
				var isReadonly = map?.IsReadOnly == true ? " readonly" : "";
				var equatable = map?.GenerateEquality == true ? $" : IEquatable<{name}>" : "";
				writer.WriteLine($"\t{visibility}{isReadonly} unsafe partial struct {name}{equatable} {{");
				var allFields = new List<string>();
				foreach (var field in klass.Fields)
				{
					var type = GetType(field.Type);
					var cppT = GetCppType(field.Type);

					writer.WriteLine($"\t\t// {field}");

					var fieldName = field.Name;
					var isPrivate = fieldName.StartsWith("_private_", StringComparison.OrdinalIgnoreCase);
					if (isPrivate)
						fieldName = fieldName.Substring(9);

					allFields.Add(fieldName);

					var vis = map?.IsInternal == true ? "public" : "private";
					var ro = map?.IsReadOnly == true ? " readonly" : "";
					writer.WriteLine($"\t\t{vis}{ro} {type} {fieldName};");

					if (!isPrivate && (map == null || (map.GenerateProperties && !map.IsInternal)))
					{
						var propertyName = fieldName;
						if (map != null && map.Members.TryGetValue(propertyName, out var fieldMap))
							propertyName = fieldMap;
						else
							propertyName = Utils.CleanName(propertyName);

						if (cppT == "bool")
						{
							if (map?.IsReadOnly == true)
							{
								writer.WriteLine($"\t\tpublic readonly bool {propertyName} => {fieldName} > 0;");
							}
							else
							{
								writer.WriteLine($"\t\tpublic bool {propertyName} {{");
								writer.WriteLine($"\t\t\treadonly get => {fieldName} > 0;");
								writer.WriteLine($"\t\t\tset => {fieldName} = value ? (byte)1 : (byte)0;");
								writer.WriteLine($"\t\t}}");
							}
						}
						else
						{
							if (map?.IsReadOnly == true)
							{
								writer.WriteLine($"\t\tpublic readonly {type} {propertyName} => {fieldName};");
							}
							else
							{
								writer.WriteLine($"\t\tpublic {type} {propertyName} {{");
								writer.WriteLine($"\t\t\treadonly get => {fieldName};");
								writer.WriteLine($"\t\t\tset => {fieldName} = value;");
								writer.WriteLine($"\t\t}}");
							}
						}
					}

					writer.WriteLine();
				}

				if (map?.GenerateEquality == true)
				{
					// IEquatable
					var equalityFields = new List<string>();
					foreach (var f in allFields)
					{
						equalityFields.Add($"{f} == obj.{f}");
					}
					writer.WriteLine($"\t\tpublic readonly bool Equals ({name} obj) =>");
					writer.WriteLine($"\t\t\t{string.Join(" && ", equalityFields)};");
					writer.WriteLine();

					// Equals
					writer.WriteLine($"\t\tpublic readonly override bool Equals (object obj) =>");
					writer.WriteLine($"\t\t\tobj is {name} f && Equals (f);");
					writer.WriteLine();

					// equality operators
					writer.WriteLine($"\t\tpublic static bool operator == ({name} left, {name} right) =>");
					writer.WriteLine($"\t\t\tleft.Equals (right);");
					writer.WriteLine();
					writer.WriteLine($"\t\tpublic static bool operator != ({name} left, {name} right) =>");
					writer.WriteLine($"\t\t\t!left.Equals (right);");
					writer.WriteLine();

					// GetHashCode
					writer.WriteLine($"\t\tpublic readonly override int GetHashCode ()");
					writer.WriteLine($"\t\t{{");
					writer.WriteLine($"\t\t\tvar hash = new HashCode ();");
					foreach (var f in allFields)
					{
						writer.WriteLine($"\t\t\thash.Add ({f});");
					}
					writer.WriteLine($"\t\t\treturn hash.ToHashCode ();");
					writer.WriteLine($"\t\t}}");
					writer.WriteLine();
				}
				writer.WriteLine($"\t}}");
			}

			writer.WriteLine();
			writer.WriteLine($"\t#endregion");
		}

		private void WriteEnums(TextWriter writer)
		{
			Log?.LogVerbose("  Writing enums...");

			writer.WriteLine($"\t#region Enums");

			var enums = compilation.Enums
				.OrderBy(c => c.GetDisplayName())
				.ToList();
			foreach (var enm in enums)
			{
				Log?.LogVerbose($"    {enm.GetDisplayName()}");

				var name = enm.GetDisplayName();
				typeMappings.TryGetValue(name, out var map);
				name = map?.CsType ?? Utils.CleanName(name);

				var visibility = "public";
				if (map?.IsInternal == true)
					visibility = "internal";

				writer.WriteLine();
				writer.WriteLine($"\t// {enm.GetDisplayName()}");
				if (map?.IsFlags == true)
					writer.WriteLine($"\t[Flags]");
				writer.WriteLine($"\t{visibility} enum {name} {{");
				foreach (var field in enm.Items)
				{
					var fieldName = field.Name;
					if (map != null && map.Members.TryGetValue(fieldName, out var fieldMap))
						fieldName = fieldMap;
					else
						fieldName = Utils.CleanName(fieldName, isEnumMember: true);

					writer.WriteLine($"\t\t// {field.Name} = {field.ValueExpression?.ToString() ?? field.Value.ToString()}");
					writer.WriteLine($"\t\t{fieldName} = {field.Value},");
				}
				writer.WriteLine($"\t}}");
			}

			writer.WriteLine();
			writer.WriteLine($"\t#endregion");
		}

		private void WriteClasses(TextWriter writer)
		{
			Log?.LogVerbose("  Writing usings...");

			writer.WriteLine($"\t#region Class declarations");
			writer.WriteLine();

			var classes = compilation.Classes
				.OrderBy(c => c.GetDisplayName())
				.ToList();
			foreach (var klass in classes)
			{
				var type = klass.GetDisplayName();
				skiaTypes.Add(type, klass.SizeOf != 0);

				if (klass.SizeOf == 0)
					writer.WriteLine($"\tusing {klass.GetDisplayName()} = IntPtr;");

				Log?.LogVerbose($"    {klass.GetDisplayName()}");
			}

			writer.WriteLine();
			writer.WriteLine($"\t#endregion");
		}

		private void WriteFunctions(TextWriter writer)
		{
			Log?.LogVerbose("  Writing p/invokes...");

			var functionGroups = compilation.Functions
				.OrderBy(f => f.Name)
				.GroupBy(f => f.Span.Start.File.ToLower().Replace("\\", "/"))
				.OrderBy(g => Path.GetDirectoryName(g.Key) + "/" + Path.GetFileName(g.Key));

			foreach (var group in functionGroups)
			{
				writer.WriteLine($"\t\t#region {Path.GetFileName(group.Key)}");
				foreach (var function in group)
				{
					Log?.LogVerbose($"    {function.Name}");

					writer.WriteLine();
					writer.WriteLine($"\t\t// {function}");
					writer.WriteLine($"\t\t[DllImport ({config.DllName}, CallingConvention = CallingConvention.Cdecl)]");

					var name = function.Name;
					functionMappings.TryGetValue(name, out var funcMap);

					var paramsList = new List<string>();
					for (var i = 0; i < function.Parameters.Count; i++)
					{
						var p = function.Parameters[i];
						var n = string.IsNullOrEmpty(p.Name) ? $"param{i}" : p.Name;
						var t = GetType(p.Type);
						var cppT = GetCppType(p.Type);
						if (cppT == "bool")
							t = $"[MarshalAs (UnmanagedType.I1)] bool";
						if (funcMap != null && funcMap.Parameters.TryGetValue(i.ToString(), out var newT))
							t = newT;
						paramsList.Add($"{t} {n}");
					}

					var returnType = GetType(function.ReturnType);
					if (funcMap != null && funcMap.Parameters.TryGetValue("-1", out var newR))
					{
						returnType = newR;
					}
					else if (GetCppType(function.ReturnType) == "bool")
					{
						returnType = "bool";
						writer.WriteLine($"\t\t[return: MarshalAs (UnmanagedType.I1)]");
					}
					writer.WriteLine($"\t\tinternal static extern {returnType} {name} ({string.Join(", ", paramsList)});");
				}
				writer.WriteLine();
				writer.WriteLine($"\t\t#endregion");
				writer.WriteLine();
			}
		}
	}
}
