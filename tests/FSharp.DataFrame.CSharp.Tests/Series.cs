﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using NUnit.Framework;
using FSharp.DataFrame;

namespace FSharp.DataFrame.CSharp.Tests
{
	class SeriesAggregation
	{
		[Test]
		public static void CanAggregateLettersUsingFloatingWindow()
		{
			var nums =
				(from n in Enumerable.Range(0, 10)
				 select KeyValue.Create(n, (char)('a' + n))).ToSeries();

			var actual = 
				nums.Aggregate(Aggregation.WindowSize<int>(5, Boundary.Skip), 
					segment => segment.Data.Keys.First(),
          segment => new string(segment.Data.Values.ToArray()));
			
			var expected =
				new SeriesBuilder<int, string> {
					{ 0, "abcde" },
					{ 1, "bcdef" },
					{ 2, "cdefg" },
					{ 3, "defgh" },
					{ 4, "efghi" },
					{ 5, "fghij" }}.Series;

			Assert.AreEqual(expected, actual);
		}

		[Test]
		public static void CanAggregateLettersUsingChunking()
		{
			var nums =
				(from n in Enumerable.Range(0, 10)
				 select KeyValue.Create(n, (char)('a' + n))).ToSeries();

			var actual =
				nums.Aggregate(Aggregation.ChunkSize<int>(5, Boundary.Skip),
					segment => segment.Data.Keys.First(),
          segment => new string(segment.Data.Values.ToArray()));

			var expected =
				new SeriesBuilder<int, string> {
					{ 0, "abcde" },
					{ 5, "fghij" }}.Series;

			Assert.AreEqual(expected, actual);
		}

		[Test]
		public static void CanAggregateLettersUsingChunkWhile()
		{
			var nums =
				new SeriesBuilder<int, char>
					{ {0, 'a'}, {10, 'b'}, {11, 'c'} }.Series;

			var actual =
				nums.Aggregate(Aggregation.ChunkWhile<int>((k1, k2) => k2 - k1 < 10),
					segment => segment.Data.Keys.First(),
          segment => new string(segment.Data.Values.ToArray()));

			var expected =
				new SeriesBuilder<int, string> {
					{ 0,  "a" },
					{ 10, "bc" }}.Series;

			Assert.AreEqual(expected, actual);
		}
	}
/*
  class Program
  {
    static void Main(string[] args)
    {
		var msft = Frame.ReadCsv(@"..\..\..\..\samples\data\msft.csv");

		var s = msft.GetSeries<double>("Open");

    IEnumerable<KeyValuePair<int, double>> kvps =
        Enumerable.Range(0, 10).Select(k => new KeyValuePair<int, double>(k, k * k));

		var series = kvps.ToSeries();
		foreach (var kvp in series.Observations)
			Console.WriteLine("{0} -> {1}", kvp.Key, kvp.Value);

		var s2 = series + series;

		Console.WriteLine(s2.Sum());

    var df = Frame.FromColumns(new[] { 1, 2, 3 }, new[] { new KeyValuePair<string, Series<int, double>>("Test", s2) });
    Console.WriteLine(((FSharp.DataFrame.Internal.IFsiFormattable)df).Format());


		// Aggregation.WindowSize(0, Boundary.AtBeginning)
    }
  }*/
}
