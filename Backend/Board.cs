﻿using System;
using System.Runtime.CompilerServices;
using Backend.Data;
using Backend.Data.Enum;
using Backend.Data.Struct;
using Backend.Data.Template;
using Backend.Engine;

namespace Backend;

public class Board
{

    public const short UBOUND = 8;
    public const short LBOUND = -1;
        
    protected const string DEFAULT_FEN = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    public PieceColor ColorToMove => Map.ColorToMove;
    public Square EnPassantTarget => Map.EnPassantTarget;
    public ulong ZobristHash => Map.ZobristHash;
    public int MaterialDevelopmentEvaluationEarly => Map.MaterialDevelopmentEvaluationEarly;
    public int MaterialDevelopmentEvaluationLate => Map.MaterialDevelopmentEvaluationLate;
    
    protected BitBoardMap Map;
        
    public static Board Default()
    {
        return FromFen(DEFAULT_FEN);
    }

    public static Board FromFen(string fen)
    {
        string[] parts = fen.Split(" ");
        return new Board(parts[0], parts[1], parts[2], parts[3]);
    }

    protected Board(Board board)
    {
        Map = board.Map.Copy();
    }

    protected Board(string boardData, string turnData, string castlingData, string enPassantTargetData)
    {
        Map = new BitBoardMap(boardData, turnData, castlingData, enPassantTargetData);
    }

    #region Readonly Properties

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (byte, byte) CastlingRight(PieceColor color) => color == PieceColor.White ? 
        (Map.WhiteQCastle, Map.WhiteKCastle) : (Map.BlackQCastle, Map.BlackKCastle);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (Piece, PieceColor) At(Square sq) => Map[sq];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BitBoard All() => Map[PieceColor.White] | Map[PieceColor.Black];
        
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BitBoard All(PieceColor color) => Map[color];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BitBoard All(Piece piece, PieceColor color) => Map[piece, color];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BitBoard KingLoc(PieceColor color) => Map[Piece.King, color];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool EmptyAt(Square sq) => Map[sq].Item1 == Piece.Empty;
        
    #endregion

    #region Move

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    // ReSharper disable once MemberCanBeProtected.Global
    public RevertMove MoveNNUE(Square from, Square to, Promotion promotion = Promotion.None)
    {
        (Piece pieceF, PieceColor colorF) = Map[from];
        (Piece pieceT, PieceColor colorT) = Map[to];

        // Generate a revert move before the map has been altered.
        RevertMove rv = RevertMove.FromBitBoardMap(ref Map);
            
        if (pieceT != Piece.Empty) {
            // If piece we're moving to isn't an empty one, we will be capturing.
            // Thus, we need to set it in revert move to ensure we can properly revert it.
            rv.CapturedPiece = pieceT;
            rv.CapturedColor = colorT;
            
            Evaluation.NNUE.EfficientlyUpdateAccumulator<Deactivate>(pieceT, colorT, to);
        }

        if (EnPassantTarget == to && pieceF == Piece.Pawn) {
            // If the attack is an EP attack, we must empty the piece affected by EP.
            Square epPieceSq = colorF == PieceColor.White ? EnPassantTarget - 8 : EnPassantTarget + 8;
            PieceColor oppositeColor = colorF.OppositeColor();
            Map.Empty(Piece.Pawn, oppositeColor, epPieceSq);
            
            Evaluation.NNUE.EfficientlyUpdateAccumulator<Deactivate>(Piece.Pawn, oppositeColor, epPieceSq);

            // Set it in revert move.
            rv.EnPassant = true;
                
            // We only need to reference the color.
            rv.CapturedColor = oppositeColor;
        }

        // Update Zobrist.
        if (EnPassantTarget != Square.Na) Zobrist.HashEp(ref Map.ZobristHash, Map.EnPassantTarget);
        if (pieceF == Piece.Pawn && Math.Abs(to - from) == 16) {
            // If the pawn push is a 2-push, the square behind it will be EP target.
            Map.EnPassantTarget = colorF == PieceColor.White ? from + 8 : from - 8;
            
            // Update Zobrist.
            Zobrist.HashEp(ref Map.ZobristHash, Map.EnPassantTarget);
        } else Map.EnPassantTarget = Square.Na;

        // Make the move.
        Map.Move(pieceF, colorF, pieceT, colorT, from, to);
        
        Evaluation.NNUE.EfficientlyUpdateAccumulator<Deactivate>(pieceF, colorF, from);
        Evaluation.NNUE.EfficientlyUpdateAccumulator<Activate>(pieceF, colorF, to);

        if (promotion != Promotion.None) {
            Map.Empty(pieceF, colorF, to);
            Map.InsertPiece(to, (Piece)promotion, colorF);
            Evaluation.NNUE.EfficientlyUpdateAccumulator<Deactivate>(pieceF, colorF, to);
            Evaluation.NNUE.EfficientlyUpdateAccumulator<Activate>((Piece)promotion, colorF, to);
            rv.Promotion = true;
        }

        // Update revert move.
        rv.From = from;
        rv.To = to;

        // Remove castling rights from hash to allow easy update.
        Zobrist.HashCastlingRights(
            ref Map.ZobristHash, 
            Map.WhiteKCastle, Map.WhiteQCastle, 
            Map.BlackKCastle, Map.BlackQCastle
        );
        
        switch (pieceF) {
            // If our rook moved, we must update castling rights.
            case Piece.Rook:
                switch (colorF) {
                    case PieceColor.White:
                        switch ((int)from % 8) {
                            case 0:
                                Map.WhiteQCastle = 0x0;
                                break;
                            case 7:
                                Map.WhiteKCastle = 0x0;
                                break;
                        }

                        break;
                    case PieceColor.Black:
                        switch ((int)from % 8) {
                            case 0:
                                Map.BlackQCastle = 0x0;
                                break;
                            case 7:
                                Map.BlackKCastle = 0x0;
                                break;
                        }

                        break;
                    case PieceColor.None:
                    default:
                        throw new InvalidOperationException("Rook cannot have no color.");
                }

                break;
                
            // If our king moved, we also must update castling rights.
            case Piece.King:
                switch (colorF) {
                    case PieceColor.White:
                        Map.WhiteKCastle = 0x0;
                        Map.WhiteQCastle = 0x0;
                        break;
                    case PieceColor.Black:
                        Map.BlackKCastle = 0x0;
                        Map.BlackQCastle = 0x0;
                        break;
                    case PieceColor.None:
                    default:
                        throw new InvalidOperationException("King cannot have no color.");
                }
                    
                int d = Math.Abs(to - from);
                if (d == 2) {
                    // In the case the king moved to castle, we must also move the rook accordingly,
                    // making a secondary move. To ensure proper reverting, we must also update our revert move.
                    if (to > from) { // King-side
                        rv.SecondaryFrom = to + 1;
                        rv.SecondaryTo = to - 1;
                    } else { // Queen-side
                        rv.SecondaryFrom = to - 2;
                        rv.SecondaryTo = to + 1;
                    }
                        
                    // Make the secondary move.
                    Map.Move(
                        Piece.Rook, colorF, Piece.Empty, PieceColor.None, 
                        rv.SecondaryFrom, rv.SecondaryTo
                    );
                    
                    Evaluation.NNUE.EfficientlyUpdateAccumulator<Deactivate>(Piece.Rook, colorF, rv.SecondaryFrom);
                    Evaluation.NNUE.EfficientlyUpdateAccumulator<Activate>(Piece.Rook, colorF, rv.SecondaryTo);
                }

                break;
            case Piece.Empty:
            case Piece.Pawn:
            case Piece.Knight:
            case Piece.Bishop:
            case Piece.Queen:
            default:
                break;
        }
            
        if (pieceT == Piece.Rook) {
            // If our rook was captured, we must also update castling rights so we don't castle with enemy piece.
            switch (colorT) {
                case PieceColor.White:
                    // ReSharper disable once ConvertIfStatementToSwitchStatement
                    if (to == Square.H1) Map.WhiteKCastle = 0x0;
                    else if (to == Square.A1) Map.WhiteQCastle = 0x0;
                    break;
                case PieceColor.Black:
                    // ReSharper disable once ConvertIfStatementToSwitchStatement
                    if (to == Square.H8) Map.BlackKCastle = 0x0;
                    else if (to == Square.A8) Map.BlackQCastle = 0x0;
                    break;
                case PieceColor.None:
                default:
                    throw new InvalidOperationException("Rook cannot have no color.");
            }
        }
        
        // Re-hash castling rights.
        Zobrist.HashCastlingRights(
            ref Map.ZobristHash, 
            Map.WhiteKCastle, Map.WhiteQCastle, 
            Map.BlackKCastle, Map.BlackQCastle
        );

        // Flip the turn.
        Map.ColorToMove = Map.ColorToMove.OppositeColor();
        
        // Update Zobrist.
        Zobrist.FlipTurnInHash(ref Map.ZobristHash);

        return rv;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public RevertMove Move(Square from, Square to, Promotion promotion = Promotion.None)
    {
        (Piece pieceF, PieceColor colorF) = Map[from];
        (Piece pieceT, PieceColor colorT) = Map[to];

        // Generate a revert move before the map has been altered.
        RevertMove rv = RevertMove.FromBitBoardMap(ref Map);
            
        if (pieceT != Piece.Empty) {
            // If piece we're moving to isn't an empty one, we will be capturing.
            // Thus, we need to set it in revert move to ensure we can properly revert it.
            rv.CapturedPiece = pieceT;
            rv.CapturedColor = colorT;
        }

        if (EnPassantTarget == to && pieceF == Piece.Pawn) {
            // If the attack is an EP attack, we must empty the piece affected by EP.
            Square epPieceSq = colorF == PieceColor.White ? EnPassantTarget - 8 : EnPassantTarget + 8;
            PieceColor oppositeColor = colorF.OppositeColor();
            Map.Empty(Piece.Pawn, oppositeColor, epPieceSq);

            // Set it in revert move.
            rv.EnPassant = true;
                
            // We only need to reference the color.
            rv.CapturedColor = oppositeColor;
        }

        // Update Zobrist.
        if (EnPassantTarget != Square.Na) Zobrist.HashEp(ref Map.ZobristHash, Map.EnPassantTarget);
        if (pieceF == Piece.Pawn && Math.Abs(to - from) == 16) {
            // If the pawn push is a 2-push, the square behind it will be EP target.
            Map.EnPassantTarget = colorF == PieceColor.White ? from + 8 : from - 8;
            
            // Update Zobrist.
            Zobrist.HashEp(ref Map.ZobristHash, Map.EnPassantTarget);
        } else Map.EnPassantTarget = Square.Na;

        // Make the move.
        Map.Move(pieceF, colorF, pieceT, colorT, from, to);

        if (promotion != Promotion.None) {
            Map.Empty(pieceF, colorF, to);
            Map.InsertPiece(to, (Piece)promotion, colorF);
            rv.Promotion = true;
        }

        // Update revert move.
        rv.From = from;
        rv.To = to;

        // Remove castling rights from hash to allow easy update.
        Zobrist.HashCastlingRights(
            ref Map.ZobristHash, 
            Map.WhiteKCastle, Map.WhiteQCastle, 
            Map.BlackKCastle, Map.BlackQCastle
        );
        
        switch (pieceF) {
            // If our rook moved, we must update castling rights.
            case Piece.Rook:
                switch (colorF) {
                    case PieceColor.White:
                        switch ((int)from % 8) {
                            case 0:
                                Map.WhiteQCastle = 0x0;
                                break;
                            case 7:
                                Map.WhiteKCastle = 0x0;
                                break;
                        }

                        break;
                    case PieceColor.Black:
                        switch ((int)from % 8) {
                            case 0:
                                Map.BlackQCastle = 0x0;
                                break;
                            case 7:
                                Map.BlackKCastle = 0x0;
                                break;
                        }

                        break;
                    case PieceColor.None:
                    default:
                        throw new InvalidOperationException("Rook cannot have no color.");
                }

                break;
                
            // If our king moved, we also must update castling rights.
            case Piece.King:
                switch (colorF) {
                    case PieceColor.White:
                        Map.WhiteKCastle = 0x0;
                        Map.WhiteQCastle = 0x0;
                        break;
                    case PieceColor.Black:
                        Map.BlackKCastle = 0x0;
                        Map.BlackQCastle = 0x0;
                        break;
                    case PieceColor.None:
                    default:
                        throw new InvalidOperationException("King cannot have no color.");
                }
                    
                int d = Math.Abs(to - from);
                if (d == 2) {
                    // In the case the king moved to castle, we must also move the rook accordingly,
                    // making a secondary move. To ensure proper reverting, we must also update our revert move.
                    if (to > from) { // King-side
                        rv.SecondaryFrom = to + 1;
                        rv.SecondaryTo = to - 1;
                    } else { // Queen-side
                        rv.SecondaryFrom = to - 2;
                        rv.SecondaryTo = to + 1;
                    }
                        
                    // Make the secondary move.
                    Map.Move(
                        Piece.Rook, colorF, Piece.Empty, PieceColor.None, 
                        rv.SecondaryFrom, rv.SecondaryTo
                    );
                }

                break;
            case Piece.Empty:
            case Piece.Pawn:
            case Piece.Knight:
            case Piece.Bishop:
            case Piece.Queen:
            default:
                break;
        }
            
        if (pieceT == Piece.Rook) {
            // If our rook was captured, we must also update castling rights so we don't castle with enemy piece.
            switch (colorT) {
                case PieceColor.White:
                    // ReSharper disable once ConvertIfStatementToSwitchStatement
                    if (to == Square.H1) Map.WhiteKCastle = 0x0;
                    else if (to == Square.A1) Map.WhiteQCastle = 0x0;
                    break;
                case PieceColor.Black:
                    // ReSharper disable once ConvertIfStatementToSwitchStatement
                    if (to == Square.H8) Map.BlackKCastle = 0x0;
                    else if (to == Square.A8) Map.BlackQCastle = 0x0;
                    break;
                case PieceColor.None:
                default:
                    throw new InvalidOperationException("Rook cannot have no color.");
            }
        }
        
        // Re-hash castling rights.
        Zobrist.HashCastlingRights(
            ref Map.ZobristHash, 
            Map.WhiteKCastle, Map.WhiteQCastle, 
            Map.BlackKCastle, Map.BlackQCastle
        );

        // Flip the turn.
        Map.ColorToMove = Map.ColorToMove.OppositeColor();
        
        // Update Zobrist.
        Zobrist.FlipTurnInHash(ref Map.ZobristHash);

        return rv;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void UndoMove(ref RevertMove rv)
    {
        // Remove castling rights from hash to allow easy update.
        Zobrist.HashCastlingRights(
            ref Map.ZobristHash, 
            Map.WhiteKCastle, Map.WhiteQCastle, 
            Map.BlackKCastle, Map.BlackQCastle
        );
        
        // Revert to old castling rights.
        Map.WhiteKCastle = rv.WhiteKCastle;
        Map.WhiteQCastle = rv.WhiteQCastle;
        Map.BlackKCastle = rv.BlackKCastle;
        Map.BlackQCastle = rv.BlackQCastle;
        
        // Re-hash castling rights.
        Zobrist.HashCastlingRights(
            ref Map.ZobristHash, 
            Map.WhiteKCastle, Map.WhiteQCastle, 
            Map.BlackKCastle, Map.BlackQCastle
        );
            
        // Update Zobrist.
        if (Map.EnPassantTarget != Square.Na) 
            Zobrist.HashEp(ref Map.ZobristHash, Map.EnPassantTarget);
        // Revert to the previous EP target.
        Map.EnPassantTarget = rv.EnPassantTarget;
        if (Map.EnPassantTarget != Square.Na) 
            // If we don't have an empty EP, we should hash it in.
            Zobrist.HashEp(ref Map.ZobristHash, Map.EnPassantTarget);

        // Revert to previous turn.
        Map.ColorToMove = rv.ColorToMove;
        Zobrist.FlipTurnInHash(ref Map.ZobristHash);

        if (rv.Promotion) {
            (Piece piece, PieceColor color) = Map[rv.To];
            Map.Empty(piece, color, rv.To);
            Map.InsertPiece(rv.To, Piece.Pawn, color);
        }
        
        (Piece pF, PieceColor cF) = Map[rv.To];
        (Piece pT, PieceColor cT) = Map[rv.From];

        // Undo the move by moving the piece back.
        Map.Move(pF, cF, pT, cT, rv.To, rv.From);
            
        if (rv.EnPassant) {
            // If it was an EP attack, we must insert a pawn at the affected square.
            Square insertion = rv.CapturedColor == PieceColor.White ? rv.To + 8 : rv.To - 8;
            Map.InsertPiece(insertion, Piece.Pawn, rv.CapturedColor);
            return;
        }

        if (rv.CapturedPiece != Piece.Empty) {
            // If a capture happened, we must insert the piece at the relevant square.
            Map.InsertPiece(rv.To, rv.CapturedPiece, rv.CapturedColor);
            return;
        }

        // If there was a secondary move (castling), revert the secondary move.
        // ReSharper disable once InvertIf
        if (rv.SecondaryFrom != Square.Na) Map.Move(rv.SecondaryTo, rv.SecondaryFrom);
    }
        
    #endregion

    #region Insert/Remove

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void InsertPiece(Square sq, Piece piece, PieceColor color) => Map.InsertPiece(sq, piece, color);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RemovePiece(Piece piece, PieceColor color, Square sq) => Map.Empty(piece, color, sq);

    #endregion

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Board Clone()
    {
        return new Board(this);
    }

    public override string ToString()
    {
        return "FEN: " + GenerateFen() + "\nHash: " + $"{Map.ZobristHash:X}" + "\n";
    }

    protected string GenerateFen()
    {
        string boardData = Map.GenerateBoardFen();
        string turnData = ColorToMove == PieceColor.White ? "w" : "b";
            
        string castlingRight = "";
        // ReSharper disable once ConvertIfStatementToSwitchStatement
        if (Map.WhiteKCastle == 0x0 && Map.WhiteQCastle == 0x0 && Map.BlackKCastle == 0x0 && Map.BlackQCastle == 0x0) {
            castlingRight = "-";
            goto EnPassantFill;
        }
            
        if (Map.WhiteKCastle != 0x0) castlingRight += "K";
        if (Map.WhiteQCastle != 0x0) castlingRight += "Q";
        if (Map.BlackKCastle != 0x0) castlingRight += "k";
        if (Map.BlackQCastle != 0x0) castlingRight += "q";
            
        EnPassantFill:
        string enPassantTarget = "-";
        if (EnPassantTarget != Square.Na) {
            enPassantTarget = EnPassantTarget.ToString().ToLower();
        }

        string[] fen = { boardData, turnData, castlingRight, enPassantTarget };
        return string.Join(" ", fen);
    }

}