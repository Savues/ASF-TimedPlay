using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

internal sealed class TypeNameProvider : ISignatureTypeProvider<string, object?> {
	public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";

	public string GetByReferenceType(string elementType) => elementType + "&";

	public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";

	public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) {
		string bare = genericType;
		int tick = bare.IndexOf('`');
		if (tick > 0) { bare = bare[..tick]; }

		// An instantiation over reference types shares code with the definition, so collapse it.
		string args = IsValueType(bare) ? string.Join(",", typeArguments) : "!";
		return bare + "<" + args + ">";
	}

	public string GetGenericMethodParameter(object? genericContext, int index) => "!!";

	public string GetGenericTypeParameter(object? genericContext, int index) => "!";

	public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

	public string GetPinnedType(string elementType) => elementType;

	public string GetPointerType(string elementType) => elementType + "*";

	public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch {
		PrimitiveTypeCode.Boolean => "bool",
		PrimitiveTypeCode.Byte => "uint8",
		PrimitiveTypeCode.Char => "char",
		PrimitiveTypeCode.Double => "float64",
		PrimitiveTypeCode.Int16 => "int16",
		PrimitiveTypeCode.Int32 => "int32",
		PrimitiveTypeCode.Int64 => "int64",
		PrimitiveTypeCode.IntPtr => "nint",
		PrimitiveTypeCode.Object => "object",
		PrimitiveTypeCode.SByte => "int8",
		PrimitiveTypeCode.Single => "float32",
		PrimitiveTypeCode.String => "string",
		PrimitiveTypeCode.TypedReference => "typedref",
		PrimitiveTypeCode.UInt16 => "uint16",
		PrimitiveTypeCode.UInt32 => "uint32",
		PrimitiveTypeCode.UInt64 => "uint64",
		PrimitiveTypeCode.UIntPtr => "nuint",
		PrimitiveTypeCode.Void => "void",
		_ => typeCode.ToString()
	};

	public string GetSZArrayType(string elementType) => elementType + "[]";

	public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) {
		var typeDef = reader.GetTypeDefinition(handle);
		string name = reader.GetString(typeDef.Name);

		if (typeDef.IsNested) {
			return GetTypeFromDefinition(reader, typeDef.GetDeclaringType(), 0) + "+" + name;
		}

		string ns = reader.GetString(typeDef.Namespace);
		return ns.Length > 0 ? ns + "." + name : name;
	}

	public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) {
		var typeRef = reader.GetTypeReference(handle);
		string name = reader.GetString(typeRef.Name);

		if (typeRef.ResolutionScope.Kind == HandleKind.TypeReference) {
			return GetTypeFromReference(reader, (TypeReferenceHandle) typeRef.ResolutionScope, 0) + "+" + name;
		}

		string ns = reader.GetString(typeRef.Namespace);
		return ns.Length > 0 ? ns + "." + name : name;
	}

	public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
		=> reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

	private static bool IsValueType(string bare) => bare switch {
		"bool" or "char" or "sbyte" or "byte" or "short" or "ushort" or "int" or "uint"
			or "long" or "ulong" or "float" or "double" or "decimal" or "nint" or "nuint"
			or "System.TimeSpan" or "System.DateTime" or "System.DateTimeOffset" or "System.Guid"
			or "System.Index" or "System.Range" or "System.ValueTuple" or "System.Nullable"
			or "System.Threading.CancellationToken" or "System.Collections.Generic.KeyValuePair" => true,
		_ => false
	};
}

internal static class Program {
	private static readonly TypeNameProvider Provider = new();

	private static int Main(string[] args) {
		if (args.Length < 2) {
			Console.Error.WriteLine("usage: ilcheck <trimmed-dir> <target-dll>");
			return 2;
		}

		string dir = args[0];
		string target = Path.GetFullPath(args[1]);
		string targetSimple = Path.GetFileNameWithoutExtension(target);

		var definitions = new HashSet<string>(StringComparer.Ordinal);
		var appCallSites = new HashSet<string>(StringComparer.Ordinal);

		foreach (string file in Directory.GetFiles(dir, "*.dll").OrderBy(Path.GetFileName, StringComparer.Ordinal)) {
			string simple = Path.GetFileNameWithoutExtension(file);

			if (simple.Equals(targetSimple, StringComparison.Ordinal) || simple.Equals("ilcheck", StringComparison.Ordinal)) {
				continue;
			}

			try {
				Scan(file, definitions, appCallSites);
			}
			catch (Exception exception) {
				Console.WriteLine($"  (skip {simple}: {exception.GetType().Name})");
			}
		}

		var targetRefs = new HashSet<string>(StringComparer.Ordinal);
		ScanCallSites(target, targetRefs);

		// ILLink keeps whatever the app actually needs, so a definition OR a call site proves survival.
		var survived = new HashSet<string>(definitions, StringComparer.Ordinal);
		survived.UnionWith(appCallSites);

		var keptByBareName = new Dictionary<string, List<string>>(StringComparer.Ordinal);

		foreach (string key in survived) {
			string bare = Bare(key);
			if (!keptByBareName.TryGetValue(bare, out List<string>? list)) {
				keptByBareName[bare] = list = [];
			}

			list.Add(key);
		}

		List<string> gone = targetRefs.Where(reference => !survived.Contains(reference))
			.OrderBy(reference => reference, StringComparer.Ordinal).ToList();

		Console.WriteLine($"kept definitions    : {definitions.Count}");
		Console.WriteLine($"app call sites      : {appCallSites.Count}");
		Console.WriteLine($"target call sites   : {targetRefs.Count}");
		Console.WriteLine();

		if (gone.Count == 0) {
			Console.WriteLine("No missing methods.");
			return 0;
		}

		Console.WriteLine($"=== {gone.Count} TRIMMED-AWAY CALL SITE(S) ===");

		foreach (string reference in gone) {
			Console.WriteLine($"  [{(appCallSites.Contains(reference) ? "used-by-ASF" : "NOT-used-by-ASF")}] {reference}");

			if (keptByBareName.TryGetValue(Bare(reference), out List<string>? survivors)) {
				Console.WriteLine("      kept overload(s) of same name:");

				foreach (string survivor in survivors.Take(8)) {
					Console.WriteLine($"        {survivor}");
				}
			}
		}

		return 1;
	}

	private static void Scan(string file, HashSet<string> definitions, HashSet<string> callSites) {
		using FileStream stream = File.OpenRead(file);
		using PEReader peReader = new(stream);

		if (!peReader.HasMetadata) {
			return;
		}

		MetadataReader reader = peReader.GetMetadataReader();

		foreach (TypeDefinitionHandle typeHandle in reader.TypeDefinitions) {
			TypeDefinition typeDef = reader.GetTypeDefinition(typeHandle);
			string typeName = Provider.GetTypeFromDefinition(reader, typeHandle, 0);

			foreach (MethodDefinitionHandle methodHandle in typeDef.GetMethods()) {
				MethodDefinition methodDef = reader.GetMethodDefinition(methodHandle);

				try {
					MethodSignature<string> signature = methodDef.DecodeSignature(Provider, null);
					definitions.Add(Format(typeName, reader.GetString(methodDef.Name), signature.ParameterTypes));
				}
				catch (Exception) {
					definitions.Add(Format(typeName, reader.GetString(methodDef.Name), []));
				}
			}
		}

		ScanCallSites(reader, callSites);
	}

	private static void ScanCallSites(string file, HashSet<string> callSites) {
		using FileStream stream = File.OpenRead(file);
		using PEReader peReader = new(stream);
		ScanCallSites(peReader.GetMetadataReader(), callSites);
	}

	private static void ScanCallSites(MetadataReader reader, HashSet<string> callSites) {
		foreach (MemberReferenceHandle handle in reader.MemberReferences) {
			MemberReference memberRef = reader.GetMemberReference(handle);

			if (memberRef.GetKind() != MemberReferenceKind.Method) {
				continue;
			}

			string? typeName = ResolveParent(reader, memberRef.Parent);

			if (typeName is null) {
				continue;
			}

			try {
				MethodSignature<string> signature = memberRef.DecodeMethodSignature(Provider, null);
				callSites.Add(Format(typeName, reader.GetString(memberRef.Name), signature.ParameterTypes));
			}
			catch (Exception) {
				// ignore undecodable signature
			}
		}
	}

	private static string? ResolveParent(MetadataReader reader, EntityHandle parent) => parent.Kind switch {
		HandleKind.TypeReference => Provider.GetTypeFromReference(reader, (TypeReferenceHandle) parent, 0),
		HandleKind.TypeDefinition => Provider.GetTypeFromDefinition(reader, (TypeDefinitionHandle) parent, 0),
		HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle) parent).DecodeSignature(Provider, null),
		_ => null
	};

	private static string Bare(string key) {
		int parenthesis = key.IndexOf('(');
		return parenthesis > 0 ? key[..parenthesis] : key;
	}

	private static string Format(string typeName, string methodName, ImmutableArray<string> parameterTypes)
		=> $"{typeName}::{methodName}({string.Join(",", parameterTypes)})";
}
