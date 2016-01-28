﻿//
// Bindings for SKColorFilter
//
// Author:
//   Matthew Leibowitz
//
// Copyright 2015 Xamarin Inc
//
using System;

namespace SkiaSharp
{
	public class SKColorFilter : IDisposable
	{
		public const int MIN_CUBE_SIZE = 4;
		public const int MAX_CUBE_SIZE = 64;

		public static bool IsValid3DColorCube(SKData cubeData, int cubeDimension)
		{
			var minMemorySize = 4 * cubeDimension * cubeDimension * cubeDimension;
			return
				(cubeDimension >= MIN_CUBE_SIZE) && (cubeDimension <= MAX_CUBE_SIZE) &&
				(null != cubeData) && (cubeData.Size >= minMemorySize);
		}

		internal IntPtr handle;

		internal SKColorFilter(IntPtr handle)
		{
			this.handle = handle;
		}

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (handle != IntPtr.Zero)
			{
				SkiaApi.sk_colorfilter_unref(handle);
				handle = IntPtr.Zero;
			}
		}

		~SKColorFilter()
		{
			Dispose(false);
		}

		public static SKColorFilter CreateXferMode(SKColor c, SKXferMode mode)
		{
			return new SKColorFilter(SkiaApi.sk_colorfilter_new_mode(c, mode));
		}

		public static SKColorFilter CreateLighting(SKColor mul, SKColor add)
		{
			return new SKColorFilter(SkiaApi.sk_colorfilter_new_lighting(mul, add));
		}

		public static SKColorFilter CreateCompose(SKColorFilter outer, SKColorFilter inner)
		{
			if (outer == null)
				throw new ArgumentNullException("outer");
			if (inner == null)
				throw new ArgumentNullException("inner");
			return new SKColorFilter(SkiaApi.sk_colorfilter_new_compose(outer.handle, inner.handle));
		}

		public static SKColorFilter CreateColorCube(byte[] cubeData, int cubeDimension)
		{
			return CreateColorCube(new SKData(cubeData), cubeDimension);
		}

		public static SKColorFilter CreateColorCube(SKData cubeData, int cubeDimension)
		{
			if (!IsValid3DColorCube(cubeData, cubeDimension))
				throw new ArgumentNullException("cubeData");
			return new SKColorFilter(SkiaApi.sk_colorfilter_new_color_cube(cubeData.handle, cubeDimension));
		}

		public static SKColorFilter CreateColorMatrix(float[] matrix)
		{
			if (matrix == null)
				throw new ArgumentNullException("matrix");
			if (matrix.Length != 20)
				throw new ArgumentException("Matrix must have a length of 20.", "matrix");
			return new SKColorFilter(SkiaApi.sk_colorfilter_new_color_matrix(matrix));
		}

		public static SKColorFilter CreateLumaColor()
		{
			return new SKColorFilter(SkiaApi.sk_colorfilter_new_luma_color());
		}

		public static SKColorFilter CreateTable(byte[] table)
		{
			if (table == null)
				throw new ArgumentNullException("table");
			if (table.Length != 256)
				throw new ArgumentException("Table must have a length of 256.", "table");
			return new SKColorFilter(SkiaApi.sk_colorfilter_new_table(table));
		}

		public static SKColorFilter CreateTable(byte[] tableA, byte[] tableR, byte[] tableG, byte[] tableB)
		{
			if (tableA != null && tableA.Length != 256)
				throw new ArgumentException("Table A must have a length of 256.", "tableA");
			if (tableR != null && tableR.Length != 256)
				throw new ArgumentException("Table R must have a length of 256.", "tableR");
			if (tableG != null && tableG.Length != 256)
				throw new ArgumentException("Table G must have a length of 256.", "tableG");
			if (tableB != null && tableB.Length != 256)
				throw new ArgumentException("Table B must have a length of 256.", "tableB");
			return new SKColorFilter(SkiaApi.sk_colorfilter_new_table_argb(tableA, tableR, tableG, tableB));
		}
	}
}
