﻿using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Http2.Protocol;
using Microsoft.Http2.Protocol.Compression.HeadersDeltaCompression;
using Microsoft.Http2.Protocol.Compression.Huffman;
using Microsoft.Http2.Protocol.Extensions;
using Microsoft.Http2.Protocol.FlowControl;
using Microsoft.Http2.Protocol.Framing;
using Microsoft.Http2.Protocol.IO;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Http2.Push;
using OpenSSL;
using Xunit;

namespace Http2.Katana.Tests
{
    public class BasicTests
    {
        private class StringComparer : IComparer<string>
        {
            int IComparer<string>.Compare(string x, string y)
            {
                return ((new CaseInsensitiveComparer()).Compare(y, x));
            }
        }

        [StandardFact]
        public void MinExtendedMathMultipleNumbersSuccessful()
        {
            var tests = new List<int[]>
                {
                    new [] {1, 0, -1, 10},
                    new [] {16383, 16383, 16383, 16383},
                    new [] {10, int.MaxValue, -100, int.MinValue},
                };

            var results = new[] { -1, 16383, int.MinValue };

            for (int i = 0; i < tests.Count; i++)
            {
                Assert.Equal(MathEx.Min(tests[i]), results[i]);
            }
        }

        [StandardFact]
        public void MinExtendedMathWithComparerSuccessful()
        {
            var tests = new List<string[]>
                {
                    new[] {"abacaba", "me", "helloworld"},
                    new[] {"get", "post", "server"},
                    new[] {"james", "teylor", "euler", "lorentz"}
                };

            var results = new[] { "me", "server", "teylor" };

            for (int i = 0; i < tests.Count; i++)
            {
                Assert.Equal(MathEx.Min(new StringComparer(), tests[i]), results[i]);
            }
        }

        [StandardFact]
        public void HeadersCollectionSuccessful()
        {
            var collection = new HeadersList(new[]
                {
                    new KeyValuePair<string, string>("myKey1", "myValue1"), 
                    new KeyValuePair<string, string>("myKey2", "myValue2"),
                    new KeyValuePair<string, string>("myKey3", "myValue3"),
                    new KeyValuePair<string, string>("myKey4", "myValue4"),
                    new KeyValuePair<string, string>("myKey5", "myValue5"),
                    new KeyValuePair<string, string>("myKey6", "myValue6"),
                    new KeyValuePair<string, string>("myKey7", "myValue7"),
                    new KeyValuePair<string, string>("myKey8", "myValue8"),
                    new KeyValuePair<string, string>("myKey9", "myValue9"),
                    new KeyValuePair<string, string>("myKey0", "myValue0")
                });

            Assert.Equal(collection.Count, 10);
            Assert.Equal(collection.StoredHeadersSize, 60 + 80 + 32 * collection.Count);

            collection.Add(new KeyValuePair<string, string>("someAddKey1", "someAddValue1"));
            collection.Add(new KeyValuePair<string, string>("someAddKey2", "someAddValue2"));

            Assert.Equal(collection.Count, 12);
            Assert.Equal(collection.StoredHeadersSize, 60 + 80 + 32 * collection.Count + 22 + 26);

            int headersSize = collection.Sum(header => header.Key.Length + header.Value.Length + 32);

            Assert.Equal(collection.StoredHeadersSize, headersSize);
        }

        [Fact]
        public void CompressionSuccessful()
        {
            var clientHeaders = new HeadersList
                {
                    new KeyValuePair<string, string>(":method", "get"),
                    new KeyValuePair<string, string>(":path", "/Y3A9NTcuNjE2NjY1fjM5Ljg2NjY2NSZsdmw9NyZzdHk9ciZxPVlhcm9zbGF2bA=="),
                    new KeyValuePair<string, string>(":version", Protocols.Http2),
                    new KeyValuePair<string, string>(":host", "localhost"),
                    new KeyValuePair<string, string>(":scheme", "https"),
                };
            var clientCompressionProc = new CompressionProcessor(ConnectionEnd.Client);
            var serverCompressionProc = new CompressionProcessor(ConnectionEnd.Server);

            var serializedHeaders = clientCompressionProc.Compress(clientHeaders);
            var decompressedHeaders = new HeadersList(serverCompressionProc.Decompress(serializedHeaders));

            foreach (var t in clientHeaders)
            {
                Assert.Equal(decompressedHeaders.GetValue(t.Key), t.Value);
            }

            var serverHeaders = new HeadersList
                {
                    new KeyValuePair<string, string>(":method", "get"),
                    new KeyValuePair<string, string>(":path", "/simpleTest.txt"),
                    new KeyValuePair<string, string>(":version", Protocols.Http2),
                    new KeyValuePair<string, string>(":host", "localhost"),
                    new KeyValuePair<string, string>(":scheme", "https"),
                };

            serializedHeaders = serverCompressionProc.Compress(serverHeaders);
            decompressedHeaders = new HeadersList(clientCompressionProc.Decompress(serializedHeaders));

            foreach (var t in serverHeaders)
            {
                Assert.Equal(decompressedHeaders.GetValue(t.Key), t.Value);
            }

            serverHeaders = new HeadersList
                {
                    new KeyValuePair<string, string>(":status", StatusCode.Code404NotFound.ToString(CultureInfo.InvariantCulture)),
                };

            serializedHeaders = serverCompressionProc.Compress(serverHeaders);
            decompressedHeaders = new HeadersList(clientCompressionProc.Decompress(serializedHeaders));

            foreach (var t in serverHeaders)
            {
                Assert.Equal(decompressedHeaders.GetValue(t.Key), t.Value);
            }
            
            serverHeaders = new HeadersList
                {
                    new KeyValuePair<string, string>("content-type", "text/plain"),
                    new KeyValuePair<string, string>("last-modified", "Wed, 23 Oct 2013 21:32:06 GMT"),
                    new KeyValuePair<string, string>("etag", "1cedo15cb041fc1"),
                    new KeyValuePair<string, string>("content-length", "749761"),
                    new KeyValuePair<string, string>(":status", StatusCode.Code200Ok.ToString(CultureInfo.InvariantCulture)),
                };

            serializedHeaders = serverCompressionProc.Compress(serverHeaders);
            decompressedHeaders = new HeadersList(clientCompressionProc.Decompress(serializedHeaders));

            foreach (var t in serverHeaders)
            {
                Assert.Equal(decompressedHeaders.GetValue(t.Key), t.Value);
            }

            clientHeaders = new HeadersList
                {
                    new KeyValuePair<string, string>(":method", "get"),
                    new KeyValuePair<string, string>(":path", "/index.html"),
                    new KeyValuePair<string, string>(":version", Protocols.Http2),
                    new KeyValuePair<string, string>(":host", "localhost"),
                    new KeyValuePair<string, string>(":scheme", "https"),
                };

            serializedHeaders = clientCompressionProc.Compress(clientHeaders);
            decompressedHeaders = new HeadersList(serverCompressionProc.Decompress(serializedHeaders));

            foreach (var t in clientHeaders)
            {
                Assert.Equal(decompressedHeaders.GetValue(t.Key), t.Value);
            }
        }

        [StandardFact]
        public void BitConverterBitsToBytesSuccessful()
        {
            const bool T = true;
            const bool F = false;
            var testInput = new List<bool>(new[] {T, T, T, F, F, F, T, F, T, F, T, F, F});
            var bytes = BinaryConverter.ToBytes(testInput);

            Assert.Equal(bytes[0], 0xe2);
            Assert.Equal(bytes[1], 0xa0);
        }

        [Fact]
        public void HuffmanCompressionSuccessful()
        {
            var compressor = new HuffmanCompressionProcessor();

            const string input = "|";/* "cabacabaababbababcacacacaedfghijklmnopqrstuvwxyz"
                                    + "Adsasd131221453!~[]{}{}~~`\'\\!@#$%^&*()_+=90klasdmnvzxcciuhakdkasdfioads"
                                    + "ADBSADLGUCJNZCXNJSLKDGYSADHIASDMNKJLDBOCXBVCXJIMSAD<NSKLDBHCBIUXHCXZNCMSN"
                                    + ",<>?|";*/

            var inputBytes = Encoding.UTF8.GetBytes(input);

            var now = DateTime.Now.Millisecond;

            var compressed = compressor.Compress(inputBytes, true);
            var decompressed = compressor.Decompress(compressed, true);

            var newNow = DateTime.Now.Millisecond;

            var output = Encoding.UTF8.GetString(decompressed);

            Assert.Equal(input, output);

            Console.WriteLine("Compress/decompress time: " + (newNow - now));
            Console.WriteLine("Input length: " + input.Length);
        }

        [StandardFact]
        public void UVarIntConversionSuccessful()
        {
            var test1337 = 1337.ToUVarInt(5);
            Assert.Equal(Int32Extensions.FromUVarInt(test1337), 1337);
            test1337 = 1337.ToUVarInt(3);
            Assert.Equal(Int32Extensions.FromUVarInt(test1337), 1337);
            test1337 = 1337.ToUVarInt(0);
            Assert.Equal(Int32Extensions.FromUVarInt(test1337), 1337);

            var test0 = 0.ToUVarInt(5);
            Assert.Equal(Int32Extensions.FromUVarInt(test0), 0);
            test0 = 0.ToUVarInt(0);
            Assert.Equal(Int32Extensions.FromUVarInt(test0), 0);

            var test0Xfffff = 0xfffff.ToUVarInt(7);
            Assert.Equal(Int32Extensions.FromUVarInt(test0Xfffff), 0xfffff);
            test0Xfffff = 0xfffff.ToUVarInt(4);
            Assert.Equal(Int32Extensions.FromUVarInt(test0Xfffff), 0xfffff);
            test0Xfffff = 0xfffff.ToUVarInt(0);
            Assert.Equal(Int32Extensions.FromUVarInt(test0Xfffff), 0xfffff);
        }

        [StandardFact]
        public void FrameHelperSuccessful()
        {
            const byte input = 1;
            byte result = FrameHelpers.SetBit(input, true, 3);
            Assert.Equal(result, 9);
            result = FrameHelpers.SetBit(result, false, 3);
            Assert.Equal(result, 1);
            result = FrameHelpers.SetBit(result, false, 0);
            Assert.Equal(result, 0);
            result = FrameHelpers.SetBit(result, true, 7);
            Assert.Equal(result, 128);
            result = FrameHelpers.SetBit(result, true, 6);
            Assert.Equal(result, 192);
            result = FrameHelpers.SetBit(result, true, 5);
            Assert.Equal(result, 224);
            result = FrameHelpers.SetBit(result, false, 7);
            Assert.Equal(result, 96);
            result = FrameHelpers.SetBit(result, false, 6);
            Assert.Equal(result, 32);
            result = FrameHelpers.SetBit(result, false, 5);
            Assert.Equal(result, 0);
            result = FrameHelpers.SetBit(result, true, 0);
            Assert.Equal(result, input);
        }

        [StandardFact]
        public void MinExtendedMathWithoutComparerSuccessful()
        {
            var tests = new List<double[]>
                {
                    new[] {1, -2, (double) 3, -6},
                    new[] {float.MaxValue, double.MinValue, byte.MaxValue, -6},
                    new[] {int.MaxValue, double.MaxValue, float.MinValue, int.MinValue}
                };

            var results = new[] { -6, double.MinValue, float.MinValue };

            for (int i = 0; i < tests.Count; i++)
            {
                Assert.Equal(MathEx.Min(tests[i]), results[i]);
            }
        }

        [StandardFact]
        public void PriorityTestSuccessful()
        {
            var itemsCollection = new List<IPriorityItem>
                {
                    new PriorityQueueEntry(null, 0),
                    new PriorityQueueEntry(null, 7),
                    new PriorityQueueEntry(null, 3),
                    new PriorityQueueEntry(null, 4),
                    new PriorityQueueEntry(null, 2),
                    new PriorityQueueEntry(null, 6),
                    new PriorityQueueEntry(null, 2),
                    new PriorityQueueEntry(null, 4),
                    new PriorityQueueEntry(null, 1),
                    new PriorityQueueEntry(null, 6),
                    new PriorityQueueEntry(null, 0),
                };

            var queue = new PriorityQueue(itemsCollection);
            Assert.Equal(queue.Count, 11);
            var firstItem1 = queue.First();
            Assert.Equal(((PriorityQueueEntry)firstItem1).Priority, 7);
            var lastItem1 = queue.Last();
            Assert.Equal(((PriorityQueueEntry)lastItem1).Priority, 0);
            var peekedItem1 = queue.Peek();
            Assert.Equal(((PriorityQueueEntry)peekedItem1).Priority, 7);
            var item1 = queue.Dequeue();
            Assert.Equal(((PriorityQueueEntry)item1).Priority, 7);
            var item2 = queue.Dequeue();
            Assert.Equal(((PriorityQueueEntry)item2).Priority, 6);
            var item3 = queue.Dequeue();
            Assert.Equal(((PriorityQueueEntry)item3).Priority, 6);
            var item4 = queue.Dequeue();
            Assert.Equal(((PriorityQueueEntry)item4).Priority, 4);
            var item5 = queue.Dequeue();
            Assert.Equal(((PriorityQueueEntry)item5).Priority, 4);
            var item6 = queue.Dequeue();
            Assert.Equal(((PriorityQueueEntry)item6).Priority, 3);
            var item7 = queue.Dequeue();
            Assert.Equal(((PriorityQueueEntry)item7).Priority, 2);
            var item8 = queue.Dequeue();
            Assert.Equal(((PriorityQueueEntry)item8).Priority, 2);
            var item9 = queue.Dequeue();
            Assert.Equal(((PriorityQueueEntry)item9).Priority, 1);
            var item10 = queue.Dequeue();
            Assert.Equal(((PriorityQueueEntry)item10).Priority,0);
            var item11 = queue.Dequeue();
            Assert.Equal(((PriorityQueueEntry)item11).Priority, 0);
        }

        [StandardFact]
        public void StreamDictionarySuccessful()
        {
            var session = new Http2Session(Stream.Null, ConnectionEnd.Client, true, true, true, new CancellationToken());
            var testCollection = session.StreamDictionary;
            var fm = new FlowControlManager(session);

            testCollection[1] = new Http2Stream(null, 1, null, fm);
            testCollection[2] = new Http2Stream(null, 2, null, fm);
            testCollection[3] = new Http2Stream(null, 3, null, fm);
            testCollection[4] = new Http2Stream(null, 4, null, fm);

            fm.DisableStreamFlowControl(testCollection[2]);
            fm.DisableStreamFlowControl(testCollection[4]);

            Assert.Equal(testCollection.NonFlowControlledStreams.Count, 2);
            Assert.Equal(testCollection.FlowControlledStreams.Count, 2);

            bool gotException = false;
            try
            {
                testCollection[4] = new Http2Stream(null, 3, null, fm);
            }
            catch (ArgumentException)
            {
                gotException = true;
            }

            Assert.Equal(gotException, true);

            testCollection.Remove(4);

            Assert.Equal(testCollection.Count, 3);
            Assert.Equal(testCollection.ContainsKey(4), false);
        }
    }
}