﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Backend.Data.Enum;
using Backend.Data.Struct;
using Backend.Data.Template;
using Backend.Engine.NNUE.Vectorization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Backend.Engine.NNUE.Architecture.Basic;

[Serializable]
public class BasicNNUE
{

    private const int INPUT = 768;
    private const int HIDDEN = 256;
    private const int OUTPUT = 1;
    private const int CR_MIN = 0;
    private const int CR_MAX = 1 * QA;
    private const int SCALE = 400;

    private const int QA = 255;
    private const int QB = 64;
    private const int QAB = QA * QB;

    private readonly short[] FeatureWeight = new short[INPUT * HIDDEN];
    private readonly short[] FlippedFeatureWeight = new short[INPUT * HIDDEN];
    private readonly short[] FeatureBias = new short[HIDDEN];
    private readonly short[] OutWeight = new short[HIDDEN * 2 * OUTPUT];
    private readonly short[] OutBias = new short[OUTPUT];

    private readonly short[] WhitePOV = new short[INPUT];
    private readonly short[] BlackPOV = new short[INPUT];

    private readonly BasicAccumulator<short>[] Accumulators = new BasicAccumulator<short>[80];

    private readonly short[] Flatten = new short[HIDDEN * 2];

    private readonly int[] Output = new int[OUTPUT];
    
    private int CurrentAccumulator;
    
    public BasicNNUE()
    {
        for (int i = 0; i < Accumulators.Length; i++) Accumulators[i] = new BasicAccumulator<short>(HIDDEN);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResetAccumulator() => CurrentAccumulator = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushAccumulator()
    {
        Accumulators.AA(CurrentAccumulator).CopyTo(Accumulators.AA(++CurrentAccumulator));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PullAccumulator() => CurrentAccumulator--;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RefreshAccumulator(Board board)
    {
        const int colorStride = 64 * 6;
        const int pieceStride = 64;
        
        Array.Clear(WhitePOV);
        Array.Clear(BlackPOV);
        
        for (PieceColor color = PieceColor.White; color < PieceColor.None; color++)
        for (Piece piece = Piece.Pawn; piece < Piece.Empty; piece++) {
            BitBoardIterator whiteIterator = board.All(piece, color).GetEnumerator();
            BitBoardIterator blackIterator = board.All(piece, color).GetEnumerator();
            Piece originalPiece = piece;
            if (piece is Piece.Rook) piece += 2;
            else if (piece is Piece.Knight or Piece.Bishop) piece--;

            Square sq = whiteIterator.Current;
            while (whiteIterator.MoveNext()) {
                int index = (int)color * colorStride + (int)piece * pieceStride + (int)sq;
                WhitePOV.AA(index) = 1;
                sq = whiteIterator.Current;
            }

            sq = blackIterator.Current;
            while (blackIterator.MoveNext()) {
                int index = (int)color.OppositeColor() * colorStride + (int)piece * pieceStride + ((int)sq ^ 56);
                BlackPOV.AA(index) = 1;
                sq = blackIterator.Current;
            }

            piece = originalPiece;
        }

        BasicAccumulator<short> accumulator = Accumulators.AA(CurrentAccumulator);

        NN.Forward(WhitePOV, FeatureWeight, accumulator.A);
        NN.Forward(BlackPOV, FeatureWeight, accumulator.B);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void EfficientlyUpdateAccumulator<Operation>(Piece piece, PieceColor color, Square sq)
        where Operation : AccumulatorOperation
    {
        const int colorStride = 64 * 6;
        const int pieceStride = 64;

        Piece nnPiece = NN.PieceToNN(piece);
        int opPieceStride = (int)nnPiece * pieceStride;

        int whiteIndex = (int)color * colorStride + opPieceStride + (int)sq;
        int blackIndex = (int)color.OppositeColor() * colorStride + opPieceStride + ((int)sq ^ 56);

        BasicAccumulator<short> accumulator = Accumulators.AA(CurrentAccumulator);

        if (typeof(Operation) == typeof(Activate)) {
            WhitePOV.AA(whiteIndex) = 1;
            BlackPOV.AA(blackIndex) = 1;
            NN.AddToAll(accumulator.A, FlippedFeatureWeight, whiteIndex * HIDDEN);
            NN.AddToAll(accumulator.B, FlippedFeatureWeight, blackIndex * HIDDEN);
        } else {
            WhitePOV.AA(whiteIndex) = 0;
            BlackPOV.AA(blackIndex) = 0;
            NN.SubtractFromAll(accumulator.A, FlippedFeatureWeight, whiteIndex * HIDDEN);
            NN.SubtractFromAll(accumulator.B, FlippedFeatureWeight, blackIndex * HIDDEN);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int Evaluate(PieceColor colorToMove)
    {
        int firstOffset = 0;
        int secondOffset = 256;

        if (colorToMove == PieceColor.Black) {
            firstOffset = 256;
            secondOffset = 0;
        }
        
        BasicAccumulator<short> accumulator = Accumulators.AA(CurrentAccumulator);
        
        NN.ClippedReLU(accumulator.A, FeatureBias, Flatten, CR_MIN, CR_MAX, firstOffset);
        NN.ClippedReLU(accumulator.B, FeatureBias, Flatten, CR_MIN, CR_MAX, secondOffset);
        
        NN.Forward(Flatten, OutWeight, Output);
        return (Output.AA(0) + OutBias.AA(0)) * SCALE / QAB;
    }

    #region JSON

    public void FromJson(Stream stream)
    {
        using JsonTextReader reader = new(new StreamReader(stream));
        
        JObject jsonObject = JObject.Load(reader);
        foreach (KeyValuePair<string, JToken> property in jsonObject) {
            switch (property.Key) {
                case "ft.weight":
                    Weight(property.Value, FeatureWeight, INPUT, QA);
                    Weight(property.Value, FlippedFeatureWeight, HIDDEN, QA, true);
                    Console.WriteLine("Feature weights loaded.");
                    break;
                case "ft.bias":
                    Bias(property.Value, FeatureBias, QA);
                    Console.WriteLine("Feature bias loaded.");
                    break;
                case "out.weight":
                    Weight(property.Value, OutWeight, HIDDEN * 2, QB);
                    Console.WriteLine("Out weights loaded.");
                    break;
                case "out.bias":
                    Bias(property.Value, OutBias, QAB);
                    Console.WriteLine("Out bias loaded.");
                    break;
            }
        }
        
        Console.WriteLine("BasicNNUE loaded from JSON.");

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Weight(JToken weightRelation, short[] weightArray, int stride, int k, bool flip = false)
        {
            int i = 0;
            foreach (JToken output in weightRelation) {
                int j = 0;
                foreach (JToken weight in output) {
                    int index;
                    if (flip) index = j * stride + i;
                    else index = i * stride + j;
                    double value = weight.ToObject<double>();
                    weightArray.AA(index) = (short)(value * k);
                    j++;
                }
                i++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Bias(JToken biasRelation, short[] biasArray, int k)
        {
            int i = 0;
            foreach (JToken bias in biasRelation) {
                double value = bias.ToObject<double>();
                biasArray.AA(i) = (short)(value * k);
                i++;
            }
        }
    }

    #endregion

}