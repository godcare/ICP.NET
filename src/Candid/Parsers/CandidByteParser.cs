﻿using EdjCase.ICP.Candid.Models;
using EdjCase.ICP.Candid.Encodings;
using EdjCase.ICP.Candid.Exceptions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using EdjCase.ICP.Candid.Models.Types;
using EdjCase.ICP.Candid.Models.Values;

namespace EdjCase.ICP.Candid.Parsers
{
    public static class CandidByteParser
    {
        public static CandidArg Parse(byte[] value)
        {
            var helper = new ByteHelper(value);
            helper.ReadMagicNumber();

            //Read type table (all compound type definitions)
            List<Func<DefinitionResolver, CandidCompoundType>> compoundDefOrRefs = helper.ReadVectorInner(() =>
            {
                DefintionOrReference t = helper.ReadType();
                if (t.Type != DefintionOrReferenceType.Compound)
                {
                    throw CandidSerializationParseException.FromReader(helper.Reader, $"Expected compound type, got '{t.Type}'");
                }
                return t.DefinitionFunc!;
            });
            DefinitionResolver resolver = new DefinitionResolver(compoundDefOrRefs);

            try
            {
                // Get all arg types
                List<CandidType> types = helper.ReadVectorInner(() =>
                {
                    DefintionOrReference t = helper.ReadType();
                    return resolver.Resolve(t);
                });
                Dictionary<CandidId, CandidCompoundType> recursiveTypes = new();

                // Get an arg value for each type
                List<CandidValueWithType> args = types
                    .Select(t => CandidValueWithType.FromValueAndType(helper.ReadValue(t, recursiveTypes), t))
                    .ToList();

                // Remaining bytes are opaque reference bytes
                byte[] opaqueReferenceBytes = value
                    .AsMemory()
                    .Slice((int)helper.Reader.BaseStream.Position)
                    .ToArray();
                return CandidArg.FromCandid(args, opaqueReferenceBytes);
            }
            catch (Exception ex) when (ex is not CandidSerializationException)
            {
                throw new Exception($"Failed to parse the candid arg. Here is the trace that it parsed so far:\n{resolver.Tracer}", ex);
            }
        }


        private class DefintionOrReference
        {
            public DefintionOrReferenceType Type { get; }
            public CandidPrimitiveType? Definition { get; }
            public Func<DefinitionResolver, CandidCompoundType>? DefinitionFunc { get; }
            public uint? ReferenceIndex { get; }
            public DefintionOrReference(
                DefintionOrReferenceType type,
                CandidPrimitiveType? definition,
                Func<DefinitionResolver, CandidCompoundType>? definitionFunc,
                uint? referenceIndex)
            {
                this.Type = type;
                this.Definition = definition;
                this.DefinitionFunc = definitionFunc;
                this.ReferenceIndex = referenceIndex;
            }

            public static DefintionOrReference Reference(uint index)
            {
                return new DefintionOrReference(DefintionOrReferenceType.Reference, null, null, index);
            }

            public static DefintionOrReference CompoundDefintion(Func<DefinitionResolver, CandidCompoundType> definitionFunc)
            {
                return new DefintionOrReference(DefintionOrReferenceType.Compound, null, definitionFunc, null);
            }

            public static DefintionOrReference Primitive(CandidPrimitiveType definition)
            {
                return new DefintionOrReference(DefintionOrReferenceType.Primitive, definition, null, null);
            }

            public override string ToString()
            {
                return this.Type switch
                {
                    DefintionOrReferenceType.Reference => $"Ref({this.ReferenceIndex!})",
                    DefintionOrReferenceType.Compound => "Compound",
                    DefintionOrReferenceType.Primitive => $"Primitive({this.Definition!.Type})",
                    _ => throw new NotImplementedException(),
                };
            }
        }

        private enum DefintionOrReferenceType
        {
            Reference,
            Compound,
            Primitive
        }

        private class DefinitionResolver
        {
            private List<TypeInfo> Types { get; }
            public CandidDebugTracer Tracer { get; }
            private int referenceCount = 0;


            public DefinitionResolver(List<Func<DefinitionResolver, CandidCompoundType>> types)
            {
                this.Types = types.Select(t => new TypeInfo(t)).ToList();
                this.Tracer = new CandidDebugTracer();
            }

            public CandidType Resolve(DefintionOrReference defOrRef)
            {
                this.Tracer.Indent();
                try
                {
                    switch (defOrRef.Type)
                    {
                        case DefintionOrReferenceType.Reference:
                            TypeInfo typeInfo = this.Types[(int)defOrRef.ReferenceIndex!];

                            // If not resolved, try to resolve
                            if (!typeInfo.ResolvingOrResolved)
                            {
                                typeInfo.ResolvingOrResolved = true; // Mark as resolving, any child with same type info 
                                typeInfo.ResolvedType = typeInfo.ResolveFunc(this); // Resolve

                                // If this type has recursion attached to it, set its resolved type 
                                // to also have a recursive type
                                if(typeInfo.RecursiveId != null)
                                {
                                    typeInfo.ResolvedType.RecursiveId = typeInfo.RecursiveId;
                                }

                                return typeInfo.ResolvedType;
                            }
                            // If already resolved, use it
                            if (typeInfo.ResolvedType != null)
                            {
                                return typeInfo.ResolvedType;
                            }
                            // If neither, then it is 'resolving', meaning it is a recursive type
                            typeInfo.RecursiveId = CandidId.Parse($"rec_{++this.referenceCount}"); // Create a new reference to mark parent object
                            this.Tracer.RecursiveReference(typeInfo.RecursiveId);
                            // Give func to resolve the type which WILL be resolved, but not yet
                            return new CandidReferenceType(typeInfo.RecursiveId);
                        case DefintionOrReferenceType.Compound:
                            return defOrRef.DefinitionFunc!(this);
                        case DefintionOrReferenceType.Primitive:
                            this.Tracer.Primitive(defOrRef.Definition!.PrimitiveType);
                            return defOrRef.Definition!;
                        default:
                            throw new NotImplementedException();
                    }
                }
                finally
                {
                    this.Tracer.Unindent();
                }
            }
            public class TypeInfo
            {
                public Func<DefinitionResolver, CandidCompoundType> ResolveFunc { get; }
                public CandidCompoundType? ResolvedType { get; set; }
                public bool ResolvingOrResolved { get; set; }
                public CandidId? RecursiveId { get; set; }

                public TypeInfo(Func<DefinitionResolver, CandidCompoundType> resolveFunc)
                {
                    this.ResolveFunc = resolveFunc ?? throw new ArgumentNullException(nameof(resolveFunc));
                    this.ResolvedType = null;
                    this.ResolvingOrResolved = false;
                    this.RecursiveId = null;
                }
            }
        }

        private class CandidDebugTracer
        {
            private StringBuilder stringBuilder = new StringBuilder();
            private int indentLevel = 0;

            public void StartCompound(string type)
            {
                this.indentLevel++;
                this.AppendIndent();
                this.stringBuilder.Append(type);
                this.stringBuilder.AppendLine(" Start");
                this.indentLevel++;
            }

            public void EndCompound(string type)
            {
                this.indentLevel--;
                this.AppendIndent();
                this.stringBuilder.Append(type);
                this.stringBuilder.AppendLine(" End");
                this.indentLevel--;
            }

            public void Primitive(PrimitiveType type)
            {
                this.indentLevel++;
                this.AppendIndent();
                this.stringBuilder.AppendLine(type.ToString());
                this.indentLevel--;
            }

            public void RecursiveReference(CandidId recursiveId)
            {
                this.indentLevel++;
                this.AppendIndent();
                this.stringBuilder.AppendLine(recursiveId.ToString());
                this.indentLevel--;
            }

            public void Field(CandidTag field)
            {
                this.indentLevel++;
                this.AppendIndent();
                this.stringBuilder.Append("Field:");
                this.stringBuilder.AppendLine(field.ToString());
                this.indentLevel--;
            }

            public void Indent()
            {
                this.indentLevel++;
            }

            public void Unindent()
            {
                this.indentLevel--;
            }

            private void AppendIndent()
            {
                for (int i = 0; i < this.indentLevel; i++)
                {
                    this.stringBuilder.Append('\t');
                }
            }

            public override string ToString()
            {
                return this.stringBuilder.ToString();
            }
        }

        private class ByteHelper : IDisposable
        {
            public BinaryReader Reader { get; }


            public ByteHelper(byte[] value)
            {
                this.Reader = new BinaryReader(new MemoryStream(value));
            }

            public void ReadMagicNumber()
            {
                byte[] magicNumber = this.Reader.ReadBytes(4);
                if (!magicNumber.SequenceEqual(new byte[] { 68, 73, 68, 76 }))
                {
                    throw CandidSerializationParseException.FromReader(this.Reader, "Bytes must start with 'DIDL' (0x68, 0x73, 0x68, 0x76)");
                }
            }

            public DefintionOrReference ReadType()
            {
                UnboundedInt typeCode = this.ReadInt();
                if (typeCode >= 0)
                {
                    return DefintionOrReference.Reference((uint)typeCode);
                }
                return this.ReadTypeInner((int)typeCode);
            }

            public DefintionOrReference ReadTypeInner(long typeCodeInt)
            {
                CandidTypeCode type = (CandidTypeCode)typeCodeInt;
                switch (type)
                {
                    case CandidTypeCode.Int:
                        return DefintionOrReference.Primitive(new CandidPrimitiveType(PrimitiveType.Int));
                    case CandidTypeCode.Int64:
                        return DefintionOrReference.Primitive(new CandidPrimitiveType(PrimitiveType.Int64));
                    case CandidTypeCode.Int32:
                        return DefintionOrReference.Primitive(new CandidPrimitiveType(PrimitiveType.Int32));
                    case CandidTypeCode.Int16:
                        return DefintionOrReference.Primitive(new CandidPrimitiveType(PrimitiveType.Int16));
                    case CandidTypeCode.Int8:
                        return DefintionOrReference.Primitive(new CandidPrimitiveType(PrimitiveType.Int8));
                    case CandidTypeCode.Nat:
                        return DefintionOrReference.Primitive(new CandidPrimitiveType(PrimitiveType.Nat));
                    case CandidTypeCode.Nat64:
                        return DefintionOrReference.Primitive(new CandidPrimitiveType(PrimitiveType.Nat64));
                    case CandidTypeCode.Nat32:
                        return DefintionOrReference.Primitive(new CandidPrimitiveType(PrimitiveType.Nat32));
                    case CandidTypeCode.Nat16:
                        return DefintionOrReference.Primitive(new CandidPrimitiveType(PrimitiveType.Nat16));
                    case CandidTypeCode.Nat8:
                        return DefintionOrReference.Primitive(new CandidPrimitiveType(PrimitiveType.Nat8));
                    case CandidTypeCode.Float32:
                        return DefintionOrReference.Primitive(new CandidPrimitiveType(PrimitiveType.Float32));
                    case CandidTypeCode.Float64:
                        return DefintionOrReference.Primitive(new CandidPrimitiveType(PrimitiveType.Float64));
                    case CandidTypeCode.Empty:
                        return DefintionOrReference.Primitive(new CandidPrimitiveType(PrimitiveType.Empty));
                    case CandidTypeCode.Null:
                        return DefintionOrReference.Primitive(new CandidPrimitiveType(PrimitiveType.Null));
                    case CandidTypeCode.Reserved:
                        return DefintionOrReference.Primitive(new CandidPrimitiveType(PrimitiveType.Reserved));
                    case CandidTypeCode.Text:
                        return DefintionOrReference.Primitive(new CandidPrimitiveType(PrimitiveType.Text));
                    case CandidTypeCode.Principal:
                        return DefintionOrReference.Primitive(new CandidPrimitiveType(PrimitiveType.Principal));
                    case CandidTypeCode.Bool:
                        return DefintionOrReference.Primitive(new CandidPrimitiveType(PrimitiveType.Bool));
                    case CandidTypeCode.Opt:
                        DefintionOrReference innerType = this.ReadType();
                        return DefintionOrReference.CompoundDefintion(resolver =>
                        {
                            resolver.Tracer.StartCompound("Opt");
                            CandidType t = resolver.Resolve(innerType);
                            resolver.Tracer.EndCompound("Opt");
                            return new CandidOptionalType(t);
                        });
                    case CandidTypeCode.Record:
                        UnboundedUInt size = this.ReadNat();
                        Dictionary<CandidTag, DefintionOrReference> recordFields = this.ReadRecordInner(size);
                        return DefintionOrReference.CompoundDefintion(resolver =>
                        {
                            var map = new Dictionary<CandidTag, CandidType>();
                            resolver.Tracer.StartCompound("Record");
                            foreach ((CandidTag field, DefintionOrReference defOrRef) in recordFields)
                            {
                                resolver.Tracer.Field(field);
                                CandidType type = resolver.Resolve(defOrRef);
                                map.Add(field, type);
                            }
                            resolver.Tracer.EndCompound("Record");
                            return new CandidRecordType(map);
                        });
                    case CandidTypeCode.Vector:
                        DefintionOrReference innerVectorType = this.ReadType();
                        return DefintionOrReference.CompoundDefintion(resolver =>
                        {
                            resolver.Tracer.StartCompound("Vector");
                            CandidType t = resolver.Resolve(innerVectorType);
                            resolver.Tracer.EndCompound("Vector");
                            return new CandidVectorType(t);
                        });
                    case CandidTypeCode.Variant:
                        UnboundedUInt length = this.ReadNat();
                        var variantOptions = new Dictionary<CandidTag, DefintionOrReference>();
                        while (length > 0)
                        {
                            length--;
                            UnboundedUInt tag = this.ReadNat();
                            DefintionOrReference option = this.ReadType();
                            variantOptions.Add((uint)tag, option);
                        }
                        return DefintionOrReference.CompoundDefintion(resolver =>
                        {
                            var map = new Dictionary<CandidTag, CandidType>();
                            resolver.Tracer.StartCompound("Variant");
                            foreach ((CandidTag field, DefintionOrReference defOrRef) in variantOptions)
                            {
                                resolver.Tracer.Field(field);
                                CandidType type = resolver.Resolve(defOrRef);
                                map.Add(field, type);
                            }
                            resolver.Tracer.StartCompound("Variant");
                            return new CandidVariantType(map);
                        });
                    case CandidTypeCode.Func:
                        List<DefintionOrReference> argTypes = this.ReadVectorInner();
                        List<DefintionOrReference> returnTypes = this.ReadVectorInner();
                        List<byte> modes = this.ReadVectorInner(() => this.ReadByte());
                        return DefintionOrReference.CompoundDefintion(resolver =>
                        {
                            resolver.Tracer.StartCompound("Func");
                            List<CandidType> a = argTypes
                                .Select(a => resolver.Resolve(a))
                                .ToList();
                            List<CandidType> r = returnTypes
                                .Select(a => resolver.Resolve(a))
                                .ToList();
                            List<FuncMode> m = modes
                                .Select(m =>
                                {
                                    resolver.Tracer.Primitive(PrimitiveType.Int8);
                                    return (FuncMode)m;
                                })
                                .ToList();
                            resolver.Tracer.EndCompound("Func");
                            return new CandidFuncType(m, a, r);
                        });
                    case CandidTypeCode.Service:
                        List<(CandidId Name, DefintionOrReference Type)> methods = this.ReadVectorInner(() =>
                        {
                            string name = this.ReadText();
                            DefintionOrReference type = this.ReadType();
                            return (CandidId.Parse(name), type);
                        });
                        return DefintionOrReference.CompoundDefintion(resolver =>
                        {
                            resolver.Tracer.StartCompound("Service");

                            Dictionary<CandidId, CandidFuncType> m = methods
                                .ToDictionary(m => m.Name, m =>
                                {
                                    var type = resolver.Resolve(m.Type);
                                    if (type is CandidFuncType f)
                                    {
                                        return f;
                                    }
                                    throw new CandidTypeResolutionException($"Service method values can only be Func types. Actual type '{type}'");
                                });
                            resolver.Tracer.EndCompound("Service");
                            return new CandidServiceType(m, id: null);
                        });
                    default:
                        throw new NotImplementedException($"Invalid type code '{typeCodeInt}'");
                };
            }

            public string ReadText()
            {
                UnboundedUInt length = this.ReadNat();
                return this.ReadTextInner(length);
            }

            public string ReadTextInner(UnboundedUInt nameLenth)
            {
                var bytes = new List<byte>();
                while (nameLenth > 0)
                {
                    bytes.Add(this.ReadByte());
                    nameLenth--;
                }
                return Encoding.UTF8.GetString(bytes.ToArray());
            }

            public byte ReadByte()
            {
                return this.Reader.ReadByte();
            }

            public Dictionary<CandidTag, DefintionOrReference> ReadRecordInner(UnboundedUInt size)
            {
                var map = new Dictionary<CandidTag, DefintionOrReference>();
                while (size > 0)
                {
                    UnboundedUInt tag = this.ReadNat();
                    DefintionOrReference type = this.ReadType();
                    map.Add(CandidTag.FromId((uint)tag), type);
                    size--;
                }
                return map;
            }

            public List<DefintionOrReference> ReadVectorInner()
            {
                return this.ReadVectorInner(() => this.ReadType());
            }

            public List<T> ReadVectorInner<T>(Func<T> func)
            {
                UnboundedUInt length = this.ReadNat();
                var items = new List<T>();
                while (length > 0)
                {
                    length--;
                    T type = func();
                    items.Add(type);
                }
                return items;
            }

            public UnboundedUInt ReadNat()
            {
                return LEB128.DecodeUnsigned(this.Reader.BaseStream);
            }
            public UnboundedInt ReadInt()
            {
                return LEB128.DecodeSigned(this.Reader.BaseStream);
            }
            public CandidValue ReadValue(CandidType typeDef, Dictionary<CandidId, CandidCompoundType> recursiveTypes)
            {
                return typeDef switch
                {
                    CandidPrimitiveType p => this.ReadPrimitive(p.PrimitiveType),
                    CandidReferenceType r => this.ReadRecursiveValue(r.Id, recursiveTypes),
                    CandidCompoundType c => this.ReadCompoundValue(c, recursiveTypes),
                    _ => throw new NotImplementedException(),
                };
            }

            public CandidValue ReadCompoundValue(CandidCompoundType c, Dictionary<CandidId, CandidCompoundType> recursiveTypes)
            {
                if (c.RecursiveId != null)
                {
                    recursiveTypes[c.RecursiveId] = c;
                }
                return c switch
                {
                    CandidOptionalType o => this.ReadOptValue(o.Value, recursiveTypes),
                    CandidVectorType ve => this.ReadVectorValue(ve.InnerType, recursiveTypes),
                    CandidRecordType r => this.ReadRecordValue(r.Fields.ToDictionary(f => f.Key, f => f.Value), recursiveTypes),
                    CandidVariantType va => this.ReadVariantValue(va.Fields.ToDictionary(f => f.Key, f => f.Value), recursiveTypes),
                    CandidServiceType s => this.ReadServiceValue(),
                    CandidFuncType f => this.ReadFuncValue(),
                    _ => throw new NotImplementedException(),
                };
            }

            public CandidValue ReadRecursiveValue(CandidId recursiveId, Dictionary<CandidId, CandidCompoundType> recursiveTypes)
            {
                CandidCompoundType? typeDef = recursiveTypes.GetValueOrDefault(recursiveId);
                if (typeDef == null)
                {
                    throw CandidSerializationParseException.FromReader(this.Reader, $"Cannot find recursive type with id '{recursiveId}'");
                }
                return this.ReadValue(typeDef, recursiveTypes);
            }

            public CandidPrimitive ReadPrimitive(PrimitiveType type)
            {
                return type switch
                {
                    PrimitiveType.Text => CandidPrimitive.Text(this.ReadText()),
                    PrimitiveType.Nat => CandidPrimitive.Nat(this.ReadNat()),
                    PrimitiveType.Nat8 => CandidPrimitive.Nat8(this.ReadByte()),
                    PrimitiveType.Nat16 => CandidPrimitive.Nat16(this.Reader.ReadUInt16()),
                    PrimitiveType.Nat32 => CandidPrimitive.Nat32(this.Reader.ReadUInt32()),
                    PrimitiveType.Nat64 => CandidPrimitive.Nat64(this.Reader.ReadUInt64()),
                    PrimitiveType.Int => CandidPrimitive.Int(this.ReadInt()),
                    PrimitiveType.Int8 => CandidPrimitive.Int8(this.ReadInt8()),
                    PrimitiveType.Int16 => CandidPrimitive.Int16(this.ReadInt16()),
                    PrimitiveType.Int32 => CandidPrimitive.Int32(this.ReadInt32()),
                    PrimitiveType.Int64 => CandidPrimitive.Int64(this.ReadInt64()),
                    PrimitiveType.Float32 => CandidPrimitive.Float32(BitConverter.ToSingle(this.Reader.ReadBytes(4), 0)),
                    PrimitiveType.Float64 => CandidPrimitive.Float64(BitConverter.ToDouble(this.Reader.ReadBytes(8), 0)),
                    PrimitiveType.Bool => CandidPrimitive.Bool(this.Reader.ReadByte() > 0),
                    PrimitiveType.Principal => CandidPrimitive.Principal(this.ReadPrincipal()),
                    PrimitiveType.Reserved => CandidPrimitive.Reserved(),
                    PrimitiveType.Empty => CandidPrimitive.Empty(),
                    PrimitiveType.Null => CandidPrimitive.Null(),
                    _ => throw new NotImplementedException(),
                };
            }

            public Principal? ReadPrincipal()
            {
                bool isRef = !this.ReadBool();
                if (isRef)
                {
                    // Opaque reference
                    return null;
                }
                else
                {
                    List<byte> bytes = this.ReadVectorInner(() => this.ReadByte());
                    return Principal.FromRaw(bytes.ToArray());
                }
            }

            public sbyte ReadInt8()
            {
                return this.Reader.ReadSByte();
            }

            public short ReadInt16()
            {
                return this.Reader.ReadInt16();
            }

            public int ReadInt32()
            {
                return this.Reader.ReadInt32();
            }

            public long ReadInt64()
            {
                return this.Reader.ReadInt64();
            }

            public bool ReadBool()
            {
                byte b = this.ReadByte();
                if (b == 0)
                {
                    return false;
                }
                if (b == 1)
                {
                    return true;
                }
                throw CandidSerializationParseException.FromReader(this.Reader, $"Expected byte with value 0 or 1, got: {b}");
            }

            public CandidValue ReadFuncValue()
            {
                bool isRef = !this.ReadBool();
                if (isRef)
                {
                    return CandidFunc.OpaqueReference();
                }
                else
                {
                    CandidService service = this.ReadServiceValue();
                    string method = this.ReadText();
                    return CandidFunc.TrasparentReference(service, method);
                }
            }

            public CandidService ReadServiceValue()
            {
                bool isRef = !this.ReadBool();
                if (isRef)
                {
                    return CandidService.OpaqueReference();
                }
                else
                {
                    Principal? principalId = this.ReadPrincipal();
                    return CandidService.TraparentReference(principalId);
                }
            }

            public CandidVariant ReadVariantValue(Dictionary<CandidTag, CandidType> options, Dictionary<CandidId, CandidCompoundType> recursiveTypes)
            {
                UnboundedUInt index = this.ReadNat();
                if (!index.TryToUInt64(out ulong i) || i > int.MaxValue)
                {
                    throw CandidSerializationParseException.FromReader(this.Reader, $"Cannot handle variants with more than '{int.MaxValue}' options");
                }
                (CandidTag tag, CandidType typeDef) = options
                    .OrderBy(f => f.Key)
                    .Select(f => (f.Key, f.Value))
                    .ElementAt((int)i);
                CandidValue value = this.ReadValue(typeDef, recursiveTypes);
                return new CandidVariant(tag, value);
            }

            public CandidRecord ReadRecordValue(Dictionary<CandidTag, CandidType> fields, Dictionary<CandidId, CandidCompoundType> recursiveTypes)
            {
                Dictionary<CandidTag, CandidValue> fieldValues = fields
                    // TODO are all the fields always there?
                    .OrderBy(f => f.Key.Id)
                    .Select(f => (f.Key, this.ReadValue(f.Value, recursiveTypes)))
                    .ToDictionary(f => f.Key, f => f.Item2);
                return new CandidRecord(fieldValues);
            }

            public CandidVector ReadVectorValue(CandidType innerTypeDef, Dictionary<CandidId, CandidCompoundType> recursiveTypes)
            {
                List<CandidValue> values = this.ReadVectorInner(() =>
                {
                    return this.ReadValue(innerTypeDef, recursiveTypes);
                });
                return new CandidVector(values.ToArray());
            }

            public CandidOptional ReadOptValue(CandidType innerTypeDef, Dictionary<CandidId, CandidCompoundType> recursiveTypes)
            {
                bool isSet = this.ReadBool();
                CandidValue? innerValue = isSet
                    ? this.ReadValue(innerTypeDef, recursiveTypes)
                    : null;
                return new CandidOptional(innerValue);
            }

            public void Dispose()
            {
                this.Reader.Dispose();
            }
        }
    }
}
