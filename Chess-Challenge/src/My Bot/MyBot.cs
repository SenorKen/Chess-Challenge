using ChessChallenge.API;
using System;
using System.Collections.Generic;

public class MyBot : IChessBot
{
    private Move bestMoveRoot = Move.NullMove;

    // Piece values for evaluation
    private readonly int[] pieceValues = { 0, 100, 310, 330, 500, 1000, 10000 };

    private readonly int[] piecePhase = { 0, 0, 1, 1, 2, 4, 0 };

    // Piece square table values for evaluation
    private readonly ulong[] pstValues = {
        657614902731556116, 420894446315227099, 384592972471695068, 312245244820264086, 364876803783607569,
        366006824779723922, 366006826859316500, 786039115310605588, 421220596516513823, 366011295806342421,
        366006826859316436, 366006896669578452, 162218943720801556, 440575073001255824, 657087419459913430,
        402634039558223453, 347425219986941203, 365698755348489557, 311382605788951956, 147850316371514514,
        329107007234708689, 402598430990222677, 402611905376114006, 329415149680141460, 257053881053295759,
        291134268204721362, 492947507967247313, 367159395376767958, 384021229732455700, 384307098409076181,
        402035762391246293, 328847661003244824, 365712019230110867, 366002427738801364, 384307168185238804,
        347996828560606484, 329692156834174227, 365439338182165780, 386018218798040211, 456959123538409047,
        347157285952386452, 365711880701965780, 365997890021704981, 221896035722130452, 384289231362147538,
        384307167128540502, 366006826859320596, 366006826876093716, 366002360093332756, 366006824694793492,
        347992428333053139, 457508666683233428, 329723156783776785, 329401687190893908, 366002356855326100,
        366288301819245844, 329978030930875600, 420621693221156179, 422042614449657239, 384602117564867863,
        419505151144195476, 366274972473194070, 329406075454444949, 275354286769374224, 366855645423297932,
        329991151972070674, 311105941360174354, 256772197720318995, 365993560693875923, 258219435335676691,
        383730812414424149, 384601907111998612, 401758895947998613, 420612834953622999, 402607438610388375,
        329978099633296596, 67159620133902
    };

    // Transposition Table Entry structure
    private struct TTEntry
    {
        public Move move;
        public int depth, score, bound;

        public TTEntry(Move _move, int _depth, int _score, int _bound)
        {
            move = _move;
            depth = _depth;
            score = _score;
            bound = _bound;
        }
    }

    private Dictionary<ulong, TTEntry> transpositionTable = new Dictionary<ulong, TTEntry>();

    /**
     * Method: GetPieceSquareTableValue
     * Access: private
     * Description: Returns the piece-square table value for the given square index.
     * Parameters:
     *      - squareIndex: The index of the square.
     * Returns: The piece-square table value for the given square index.
     */
    private int GetPieceSquareTableValue(int squareIndex)
    {
        return (int)(((pstValues[squareIndex / 10] >> (6 * (squareIndex % 10))) & 63) - 20) * 8;
    }

    /**
     * Method: Search
     * Access: private
     * Description: Performs a negamax search with alpha-beta pruning and iterative deepening to find the best move.
     * Parameters:
     *      - board: The current board state.
     *      - timer: The timer for the current turn.
     *      - alpha: The alpha value for alpha-beta pruning.
     *      - beta: The beta value for alpha-beta pruning.
     *      - depth: The current depth of the search.
     *      - ply: The current ply of the search.
     * Returns: The score of the best move found.
     */
    private int Search(Board board, Timer timer, int alpha, int beta, int depth, int ply)
    {
        var key = board.ZobristKey;
        var isQuiescenceSearch = depth <= 0;
        var isNotRoot = ply > 0;
        var bestScore = int.MinValue;

        // Check for repetition (this is much more important than material and 50-move rule draws)
        if (isNotRoot && board.IsRepeatedPosition())
            return 0;

        // Transposition Table lookup
        if (transpositionTable.TryGetValue(key, out var entry) && entry.depth >= depth && (entry.bound == 3 ||
                                                                      entry.bound == 2 && entry.score >= beta ||
                                                                      entry.bound == 1 && entry.score <= alpha))
            return entry.score;

        int midgameScore = 0, endgameScore = 0, phase = 0;

        foreach (var isWhite in new[] { true, false })
        {
            for (var pieceType = PieceType.Pawn; pieceType <= PieceType.King; pieceType++)
            {
                int piece = (int)pieceType, index;
                var mask = board.GetPieceBitboard(pieceType, isWhite);
                while (mask != 0)
                {
                    phase += piecePhase[piece];
                    index = (128 * (piece - 1) + BitboardHelper.ClearAndGetIndexOfLSB(ref mask)) ^ (isWhite ? 56 : 0);
                    midgameScore += GetPieceSquareTableValue(index) + pieceValues[piece];
                    endgameScore += GetPieceSquareTableValue(index + 64) + pieceValues[piece];
                }
            }

            midgameScore = -midgameScore;
            endgameScore = -endgameScore;
        }

        var evaluation = (midgameScore * phase + endgameScore * (24 - phase)) / 24 * (board.IsWhiteToMove ? 1 : -1);

        // Quiescence search is in the same function as negamax to save tokens
        if (isQuiescenceSearch)
        {
            bestScore = evaluation;
            if (bestScore >= beta) return bestScore;
            alpha = Math.Max(alpha, bestScore);
        }

        // Generate moves, only captures in quiescence search
        var moves = board.GetLegalMoves(isQuiescenceSearch);
        var moveScores = new int[moves.Length];

        // Score moves
        for (var i = 0; i < moves.Length; i++)
        {
            var move = moves[i];
            // Transposition Table move
            if (move == entry.move)
                moveScores[i] = 1000000;
            // MVV-LVA (Most Valuable Victim - Least Valuable Attacker)
            else if (move.IsCapture) moveScores[i] = 100 * (int)move.CapturePieceType - (int)move.MovePieceType;
        }

        var bestMove = Move.NullMove;
        var originalAlpha = alpha;

        // Search moves
        for (var i = 0; i < moves.Length; i++)
        {
            if (timer.MillisecondsElapsedThisTurn >= timer.MillisecondsRemaining / 30) return 30000;

            // Incrementally sort moves
            for (var j = i + 1; j < moves.Length; j++)
                if (moveScores[j] > moveScores[i])
                    (moveScores[i], moveScores[j], moves[i], moves[j]) =
                        (moveScores[j], moveScores[i], moves[j], moves[i]);

            var move = moves[i];
            board.MakeMove(move);
            var score = -Search(board, timer, -beta, -alpha, depth - 1, ply + 1);
            board.UndoMove(move);

            // New best move
            if (score > bestScore)
            {
                bestScore = score;
                bestMove = move;
                if (ply == 0) bestMoveRoot = move;

                // Improve alpha
                alpha = Math.Max(alpha, score);

                // Fail-high
                if (alpha >= beta) break;
            }

            //Console.WriteLine(move + ", Score: " + score);
        }

        // (Check/Stale)mate
        if (!isQuiescenceSearch && moves.Length == 0) return board.IsInCheck() ? -30000 + ply : 0;

        // Did we fail high/low or get an exact score?
        var bound = bestScore >= beta ? 2 : bestScore > originalAlpha ? 3 : 1;

        // Push to Transposition Table
        transpositionTable[key] = new TTEntry(bestMove, depth, bestScore, bound);

        return bestScore;
    }
    
     /**
     * Method: Think
     * Access: public
     * Description: Finds the best move for the current board state.
     * Parameters:
     *      - board: The current board state.
     *      - timer: The timer for the current turn.
     * Returns: The best move found.
     */
    public Move Think(Board board, Timer timer)
    {
        bestMoveRoot = Move.NullMove;
        var score = 0;
        // Iterative Deepening
        for (var depth = 1; depth <= 64; depth++)
        {
            score = Search(board, timer, -30000, 30000, depth, 0);

            // Out of time
            if (timer.MillisecondsElapsedThisTurn >= timer.MillisecondsRemaining / 30) break;
        }

        //Console.WriteLine("Best move: " + bestMoveRoot + ", Score: " + score);
        return bestMoveRoot.IsNull ? board.GetLegalMoves()[0] : bestMoveRoot;
    }
}
