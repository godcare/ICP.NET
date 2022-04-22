﻿using Common.Models;
using ICP.Common.Candid;
using ICP.Common.Encodings;
using ICP.Common.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Common.Candid
{
    public class CandidReader : IDisposable
    {
        private readonly BinaryReader reader;
        public CandidReader(byte[] value)
        {
            this.reader = new BinaryReader(new MemoryStream(value));
        }

        public void ReadMagicNumber()
        {
            byte[] magicNumber = this.reader.ReadBytes(4);
            if(!magicNumber.SequenceEqual(new byte[] { 68, 73, 68, 76 }))
            {
                // TODO
                throw new Exception();
            }
        }
        public CompoundTypeTable ReadArgTypeTable()
        {
            LEB128 typeCount = this.ReadLEB128();
            var types = new List<CandidTypeDefinition>();
            while (typeCount > 0)
            {
                CandidTypeDefinition typeDef = this.ReadType();
                types.Add(typeDef);
                typeCount--;
            }

            return CompoundTypeTable.FromTypes(types);
        }

        private CandidTypeDefinition ReadType()
        {
            throw new NotImplementedException();
        }

        private LEB128 ReadLEB128()
        {
            while (true)
            {
                byte b = this.reader.ReadByte();
            }
        }

        public List<CandidTypeDefinition> ReadArgTypes(CompoundTypeTable typeTable)
        {

        }

        public List<CandidValue> ReadArgValues(List<CandidTypeDefinition> types)
        {

        }


        public void Dispose()
        {
            this.reader.Dispose();
        }


        public static List<(CandidValue, CandidTypeDefinition)> ReadArg(byte[] value)
        {
            if (value.Length < 5)
            {
                // TODO
                throw new Exception();
            }
            var reader = new CandidReader(value);
            reader.ReadMagicNumber();
            CompoundTypeTable compoundTypeTable = reader.ReadArgTypeTable();
            List<CandidTypeDefinition> types = reader.ReadArgTypes(compoundTypeTable);
            List<CandidValue> values = reader.ReadArgValues(types);
            return values
                .Zip(types)
                .ToList();
        }
    }
}
