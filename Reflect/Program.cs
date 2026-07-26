// Lists BCL members a given assembly's IL actually references, by reading its metadata directly
// (no need to load or execute the assembly). Used to check, before relying on a BCL method under
// ASF's trimmed runtime, whether ASF's own code calls that method anywhere — if it does, the
// trimmer had to keep it. A member missing from this list is inconclusive (it may still be kept
// via some other assembly in the trimmed closure); a member present in this list is proof.
//
// Usage: dotnet run --project Reflect -- [path/to/Assembly.dll] [filter substring]
// With no filter, prints every distinct "Namespace.Type::Member" the assembly references.

using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

string assemblyPath = args.Length > 0 ? args[0] : Path.Combine("..", "lib", "ArchiSteamFarm.dll");
string? filter = args.Length > 1 ? args[1] : null;

using FileStream fs = File.OpenRead(assemblyPath);
using PEReader pe = new(fs);
MetadataReader reader = pe.GetMetadataReader();

TypeNameProvider provider = new();
SortedSet<string> members = new(StringComparer.Ordinal);

foreach (MemberReferenceHandle handle in reader.MemberReferences) {
	MemberReference member = reader.GetMemberReference(handle);
	string memberName = reader.GetString(member.Name);

	string? typeName = member.Parent.Kind switch {
		HandleKind.TypeReference => provider.GetTypeFromReference(reader, (TypeReferenceHandle) member.Parent, 0),
		HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle) member.Parent).DecodeSignature(provider, null),
		_ => null
	};

	if (typeName == null) {
		continue;
	}

	// Include the parameter list for methods so overloads are distinguishable — knowing *a*
	// IndexOf overload is referenced isn't enough; the trimmer preserves specific overloads, not names.
	string signature = memberName;

	if (member.GetKind() == MemberReferenceKind.Method) {
		try {
			MethodSignature<string> decoded = member.DecodeMethodSignature(provider, null);

			signature = $"{memberName}({string.Join(", ", decoded.ParameterTypes)})";
		} catch {
			// Some signatures (vararg, unusual calling conventions) aren't worth failing the whole run over.
		}
	}

	string entry = $"{typeName}::{signature}";

	if ((filter == null) || entry.Contains(filter, StringComparison.OrdinalIgnoreCase)) {
		members.Add(entry);
	}
}

Console.WriteLine($"=== {Path.GetFileName(assemblyPath)}: {members.Count} referenced member(s){(filter != null ? $" matching \"{filter}\"" : "")} ===");

foreach (string entry in members) {
	Console.WriteLine("  " + entry);
}

// Minimal ISignatureTypeProvider so generic instantiations (e.g. List<uint>) resolve to a readable
// name instead of being skipped; nothing here needs to round-trip back into a real Type.
internal sealed class TypeNameProvider : ISignatureTypeProvider<string, object?> {
	public string GetTypeFromReference(MetadataReader md, TypeReferenceHandle handle, byte rawTypeKind) {
		TypeReference tr = md.GetTypeReference(handle);
		string ns = md.GetString(tr.Namespace);
		string name = md.GetString(tr.Name);

		return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
	}

	public string GetTypeFromDefinition(MetadataReader md, TypeDefinitionHandle handle, byte rawTypeKind) => md.GetString(md.GetTypeDefinition(handle).Name);
	public string GetTypeFromSpecification(MetadataReader md, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => md.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
	public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => $"{genericType}<{string.Join(",", typeArguments)}>";
	public string GetSZArrayType(string elementType) => elementType + "[]";
	public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
	public string GetByReferenceType(string elementType) => elementType + "&";
	public string GetPointerType(string elementType) => elementType + "*";
	public string GetPinnedType(string elementType) => elementType;
	public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
	public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
	public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
	public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
	public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
}
