﻿namespace FSharp.DataFrame

// --------------------------------------------------------------------------------------
// Data frame
// --------------------------------------------------------------------------------------

open FSharp.DataFrame
open FSharp.DataFrame.Keys
open FSharp.DataFrame.Internal
open FSharp.DataFrame.Indices
open FSharp.DataFrame.Vectors

open System
open System.ComponentModel
open System.Runtime.InteropServices
open VectorHelpers

/// A frame contains one Index, with multiple Vecs
/// (because this is dynamic, we need to store them as IVec)
type Frame<'TRowKey, 'TColumnKey when 'TRowKey : equality and 'TColumnKey : equality>
    internal ( rowIndex:IIndex<'TRowKey>, columnIndex:IIndex<'TColumnKey>, 
               data:IVector<IVector>) =

  // ----------------------------------------------------------------------------------------------
  // Internals (rowIndex, columnIndex, data and various helpers)
  // ----------------------------------------------------------------------------------------------

  let mutable isEmpty = rowIndex.IsEmpty && columnIndex.IsEmpty

  /// Vector builder
  let vectorBuilder = Vectors.ArrayVector.ArrayVectorBuilder.Instance
  let indexBuilder = Indices.Linear.LinearIndexBuilder.Instance

  // TODO: Perhaps assert that the 'data' vector has all things required by column index
  // (to simplify various handling below)

  let mutable rowIndex = rowIndex
  let mutable columnIndex = columnIndex
  let mutable data = data

  let createRowReader rowAddress =
    // 'let rec' would be more elegant, but it is slow...
    let virtualVector = ref (Unchecked.defaultof<_>)
    let materializeVector() =
      let data = (virtualVector : ref<IVector<_>>).Value.DataSequence
      virtualVector := Vector.ofOptionalValues(data)
    virtualVector :=
      { new IVector<obj> with
          member x.GetValue(columnAddress) = 
            let vector = data.GetValue(columnAddress)
            if not vector.HasValue then OptionalValue.Missing
            else vector.Value.GetObject(rowAddress) 
          member x.Data = 
            [| for _, addr in columnIndex.Mappings -> x.GetValue(addr) |]
            |> IReadOnlyList.ofArray |> VectorData.SparseList          
          member x.Select(f) = materializeVector(); virtualVector.Value.Select(f)
          member x.SelectMissing(f) = materializeVector(); virtualVector.Value.SelectMissing(f)
        
        interface IVector with
          member x.SuppressPrinting = false
          member x.ElementType = typeof<obj>
          member x.GetObject(i) = (x :?> IVector<obj>).GetValue(i) }
    VectorHelpers.delegatedVector virtualVector

  let safeGetRowVector row = 
    let rowVect = rowIndex.Lookup(row)
    if not rowVect.HasValue then invalidArg "index" (sprintf "The data frame does not contain row with index '%O'" row) 
    else  createRowReader (snd rowVect.Value)

  let safeGetColVector column = 
    let columnIndex = columnIndex.Lookup(column)
    if not columnIndex.HasValue then 
      invalidArg "column" (sprintf "Column with a key '%O' does not exist in the data frame" column)
    let columnVector = data.GetValue (snd columnIndex.Value)
    if not columnVector.HasValue then
      invalidOp "column" (sprintf "Column with a key '%O' is present, but does not contain a value" column)
    columnVector.Value
  
  member private x.tryGetColVector column = 
    let columnIndex = columnIndex.Lookup(column)
    if not columnIndex.HasValue then OptionalValue.Missing else
    data.GetValue (snd columnIndex.Value)
  member internal x.IndexBuilder = indexBuilder
  member internal x.VectorBuilder = vectorBuilder

  member internal frame.RowIndex = rowIndex
  member internal frame.ColumnIndex = columnIndex
  member internal frame.Data = data

  // ----------------------------------------------------------------------------------------------
  // Joining and appending
  // ----------------------------------------------------------------------------------------------

  /// [category:Joining]
  member frame.Join(otherFrame:Frame<'TRowKey, 'TColumnKey>, ?kind, ?lookup) =    
    let lookup = defaultArg lookup Lookup.Exact

    let restrictToRowIndex (restriction:IIndex<_>) (sourceIndex:IIndex<_>) vector = 
      if restriction.Ordered && sourceIndex.Ordered then
        let min, max = rowIndex.KeyRange
        sourceIndex.Builder.GetRange(sourceIndex, Some(min, BoundaryBehavior.Inclusive), Some(max, BoundaryBehavior.Inclusive), vector)
      else sourceIndex, vector

    // Union row indices and get transformations to apply to left/right vectors
    let newRowIndex, thisRowCmd, otherRowCmd = 
      match kind with 
      | Some JoinKind.Inner ->
          indexBuilder.Intersect( (rowIndex, Vectors.Return 0), (otherFrame.RowIndex, Vectors.Return 0) )
      | Some JoinKind.Left ->
          let otherRowIndex, vector = restrictToRowIndex rowIndex otherFrame.RowIndex (Vectors.Return 0)
          let otherRowCmd = indexBuilder.Reindex(otherRowIndex, rowIndex, lookup, vector)
          rowIndex, Vectors.Return 0, otherRowCmd
      | Some JoinKind.Right ->
          let thisRowIndex, vector = restrictToRowIndex otherFrame.RowIndex rowIndex (Vectors.Return 0)
          let thisRowCmd = indexBuilder.Reindex(thisRowIndex, otherFrame.RowIndex, lookup, vector)
          otherFrame.RowIndex, thisRowCmd, Vectors.Return 0
      | Some JoinKind.Outer | None | Some _ ->
          indexBuilder.Union( (rowIndex, Vectors.Return 0), (otherFrame.RowIndex, Vectors.Return 0) )

    // Append the column indices and get transformation to combine them
    // (LeftOrRight - specifies that when column exist in both data frames then fail)
    let newColumnIndex, colCmd = 
      indexBuilder.Append( (columnIndex, Vectors.Return 0), (otherFrame.ColumnIndex, Vectors.Return 1), VectorValueTransform.LeftOrRight)
    // Apply transformation to both data vectors
    let newThisData = data.Select(transformColumn vectorBuilder thisRowCmd)
    let newOtherData = otherFrame.Data.Select(transformColumn vectorBuilder otherRowCmd)
    // Combine column vectors a single vector & return results
    let newData = vectorBuilder.Build(colCmd, [| newThisData; newOtherData |])
    Frame(newRowIndex, newColumnIndex, newData)

  /// [category:Joining]
  member frame.Append(otherFrame:Frame<'TRowKey, 'TColumnKey>) = 
    // Union the column indices and get transformations for both
    let newColumnIndex, thisColCmd, otherColCmd = 
      indexBuilder.Union( (columnIndex, Vectors.Return 0), (otherFrame.ColumnIndex, Vectors.Return 1) )

    // Append the row indices and get transformation that combines two column vectors
    // (LeftOrRight - specifies that when column exist in both data frames then fail)
    let newRowIndex, rowCmd = 
      indexBuilder.Append( (rowIndex, Vectors.Return 0), (otherFrame.RowIndex, Vectors.Return 1), VectorValueTransform.LeftOrRight)

    // Transform columns - if we have both vectors, we need to append them
    let appendVector = 
      { new VectorHelpers.VectorCallSite1<IVector -> IVector> with
          override x.Invoke<'T>(col1:IVector<'T>) = (fun col2 ->
            let col2 = VectorHelpers.changeType<'T> col2
            vectorBuilder.Build(rowCmd, [| col1; col2 |]) :> IVector) }
      |> VectorHelpers.createVectorDispatcher
    // .. if we only have one vector, we need to pad it 
    let padVector isLeft = 
      { new VectorHelpers.VectorCallSite1<IVector> with
          override x.Invoke<'T>(col:IVector<'T>) = 
            let empty = Vector.ofValues []
            let args = if isLeft then [| col; empty |] else [| empty; col |]
            vectorBuilder.Build(rowCmd, args) :> IVector }
      |> VectorHelpers.createVectorDispatcher
    let padLeftVector, padRightVector = padVector true, padVector false

    let append = VectorValueTransform.Create(fun (l:OptionalValue<IVector>) r ->
      if l.HasValue && r.HasValue then OptionalValue(appendVector l.Value r.Value)
      elif l.HasValue then OptionalValue(padLeftVector l.Value)
      elif r.HasValue then OptionalValue(padRightVector r.Value)
      else OptionalValue.Missing )

    let newDataCmd = Vectors.Combine(thisColCmd, otherColCmd, append)
    let newData = vectorBuilder.Build(newDataCmd, [| data; otherFrame.Data |])

    Frame(newRowIndex, newColumnIndex, newData)

  // ----------------------------------------------------------------------------------------------
  // Frame accessors
  // ----------------------------------------------------------------------------------------------

  /// [category:Accessors]
  member frame.IsEmpty = 
    rowIndex.Mappings |> Seq.isEmpty

  /// [category:Accessors]
  member frame.RowKeys = rowIndex.Keys
  /// [category:Accessors]
  member frame.ColumnKeys = columnIndex.Keys

  /// [category:Accessors]
  member frame.Columns = 
    ColumnSeries(Series.Create(columnIndex, data.Select(fun vect -> 
      Series.CreateUntyped(rowIndex, boxVector vect))))

  /// [category:Accessors]
  member frame.ColumnsDense = 
    ColumnSeries(Series.Create(columnIndex, data.SelectMissing(fun vect -> 
      // Assuming that the data has all values - which should be an invariant...
      let all = rowIndex.Mappings |> Seq.forall (fun (key, addr) -> vect.Value.GetObject(addr).HasValue)
      if all then OptionalValue(Series.CreateUntyped(rowIndex, boxVector vect.Value))
      else OptionalValue.Missing )))

  /// [category:Accessors]
  member frame.Rows = 
    let emptySeries = Series<_, _>(rowIndex, Vector.ofValues [], vectorBuilder, indexBuilder)
    let res = emptySeries.SelectOptional (fun row ->
      let rowAddress = rowIndex.Lookup(row.Key, Lookup.Exact, fun _ -> true)
      if not rowAddress.HasValue then OptionalValue.Missing
      else OptionalValue(Series.CreateUntyped(columnIndex, createRowReader (snd rowAddress.Value))))
    RowSeries(res)

  /// [category:Accessors]
  member frame.RowsDense = 
    let emptySeries = Series<_, _>(rowIndex, Vector.ofValues [], vectorBuilder, indexBuilder)
    let res = emptySeries.SelectOptional (fun row ->
      let rowAddress = rowIndex.Lookup(row.Key, Lookup.Exact, fun _ -> true)
      if not rowAddress.HasValue then OptionalValue.Missing else 
        let rowVec = createRowReader (snd rowAddress.Value)
        let all = columnIndex.Mappings |> Seq.forall (fun (key, addr) -> rowVec.GetValue(addr).HasValue)
        if all then OptionalValue(Series.CreateUntyped(columnIndex, rowVec))
        else OptionalValue.Missing )
    RowSeries(res)

  /// [category:Accessors]
  member frame.Item 
    with get(column:'TColumnKey, row:'TRowKey) = frame.Columns.[column].[row]

  // ----------------------------------------------------------------------------------------------
  // More accessors
  // ----------------------------------------------------------------------------------------------

  /// [category:Fancy accessors]
  member frame.GetColumns<'R>() = 
    frame.Columns.SelectOptional(fun (KeyValue(k, vopt)) ->
      vopt |> OptionalValue.bind (fun ser -> ser.TryAs<'R>()))

  /// [category:Fancy accessors]
  member frame.GetRow<'R>(row) = frame.GetRow<'R>(row, Lookup.Exact)

  /// [category:Fancy accessors]
  member frame.GetRow<'R>(row, lookup) : Series<'TColumnKey, 'R> = 
    let row = frame.Rows.Get(row, lookup)
    Series.Create(columnIndex, changeType row.Vector)

  // ----------------------------------------------------------------------------------------------
  // Series related operations - add, drop, get, ?, ?<-, etc.
  // ----------------------------------------------------------------------------------------------

  /// [category:Series operations]
  member frame.Item 
    with get(column:'TColumnKey) = frame.GetSeries<float>(column)
    and set(column:'TColumnKey) (series:Series<'TRowKey, float>) = frame.ReplaceSeries(column, series)

  /// [category:Series operations]
  member frame.AddSeries(column:'TColumnKey, series:seq<_>) = 
    frame.AddSeries(column, series, Lookup.Exact)

  /// [category:Series operations]
  member frame.AddSeries(column:'TColumnKey, series:ISeries<_>) = 
    frame.AddSeries(column, series, Lookup.Exact)

  /// [category:Series operations]
  member frame.AddSeries(column:'TColumnKey, series:seq<'V>, lookup) = 
    if isEmpty then
      if typeof<'TRowKey> = typeof<int> then
        let series = unbox<Series<'TRowKey, 'V>> (Series.ofValues series)
        frame.AddSeries(column, series)
      else
        invalidOp "Adding sequence to an empty frame with non-integer columns is not supported."
    else
      let count = Seq.length series
      let rowCount = Seq.length frame.RowIndex.Keys
      // Pad with missing values, if there is not enough, or trim if there is more
      let vector = 
        if count >= rowCount then 
          Vector.ofValues (Seq.take count series)
        else
          let nulls = seq { for i in 1 .. rowCount - count -> None }
          Vector.ofOptionalValues (Seq.append (Seq.map Some series) nulls)

      let series = Series(frame.RowIndex, vector, vectorBuilder, indexBuilder)
      frame.AddSeries(column, series, lookup)

  /// [category:Series operations]
  member frame.AddSeries<'V>(column:'TColumnKey, series:ISeries<'TRowKey>, lookup) = 
    if isEmpty then
      // If the frame was empty, then initialize both indices
      rowIndex <- series.Index
      columnIndex <- Index.ofKeys [column]
      data <- Vector.ofValues [series.Vector]
      isEmpty <- false
    else
      let other = Frame(series.Index, Index.ofUnorderedKeys [column], Vector.ofValues [series.Vector])
      let joined = frame.Join(other, JoinKind.Left, lookup)
      columnIndex <- joined.ColumnIndex
      data <- joined.Data

  /// [category:Series operations]
  member frame.DropSeries(column:'TColumnKey) = 
    let newColumnIndex, colCmd = indexBuilder.DropItem( (columnIndex, Vectors.Return 0), column)
    columnIndex <- newColumnIndex
    data <- vectorBuilder.Build(colCmd, [| data |])

  /// [category:Series operations]
  member frame.ReplaceSeries(column:'TColumnKey, series:ISeries<_>, ?lookup) = 
    let lookup = defaultArg lookup Lookup.Exact
    if columnIndex.Lookup(column, lookup, fun _ -> true).HasValue then
      frame.DropSeries(column)
    frame.AddSeries(column, series)

  /// [category:Series operations]
  member frame.ReplaceSeries(column, data:seq<'V>) = 
    frame.ReplaceSeries(column, Series.Create(frame.RowIndex, Vector.ofValues data))

  /// [category:Series operations]
  member frame.GetSeries<'R>(column:'TColumnKey, lookup) : Series<'TRowKey, 'R> = 
    match safeGetColVector(column, lookup, fun _ -> true) with
    | :? IVector<'R> as vec -> 
        Series.Create(rowIndex, vec)
    | colVector ->
        Series.Create(rowIndex, changeType colVector)

  /// [category:Series operations]
  member frame.GetSeriesAt<'R>(index:int) : Series<'TRowKey, 'R> = 
    frame.Columns.GetAt(index).As<'R>()

  /// [category:Series operations]
  member frame.GetSeries<'R>(column:'TColumnKey) : Series<'TRowKey, 'R> = 
    frame.GetSeries(column, Lookup.Exact)

  /// [category:Series operations]
  static member (?<-) (frame:Frame<_, _>, column, series:Series<'T, 'V>) =
    frame.ReplaceSeries(column, series)

  /// [category:Series operations]
  static member (?<-) (frame:Frame<_, _>, column, data:seq<'V>) =
    frame.ReplaceSeries(column, data)

  /// [category:Series operations]
  static member (?) (frame:Frame<_, _>, column) : Series<'T, float> = 
    frame.GetSeries<float>(column)

  // ----------------------------------------------------------------------------------------------
  // Some operators
  // ----------------------------------------------------------------------------------------------

  /// This pretty much duplicates `FrameUtils.ofColumns`, but we need to inline it here,
  /// otherwise the type inference breaks in very bad ways :-(
  static member private FromColumnsNonGeneric (nested:Series<_, ISeries<_>>) = 
    let columns = Series.observations nested
    let rowIndex = (Seq.head columns |> snd).Index
    if (columns |> Seq.forall (fun (_, s) -> Object.ReferenceEquals(s.Index, rowIndex))) then
      // OPTIMIZATION: If all series have the same index (same object), then no join is needed 
      // (This is particularly valuable for things like +, *, /, - operators on Frame)
      let vector = columns |> Seq.map (fun (_, s) -> s.Vector) |> Vector.ofValues
      Frame<_, _>(rowIndex, Index.ofKeys (Seq.map fst columns), vector)
    else
      let initial = Frame(Index.ofKeys [], Index.ofUnorderedKeys [], Vector.ofValues [| |])
      (initial, Series.observations nested) ||> Seq.fold (fun df (column, (series:ISeries<_>)) -> 
        let data = Vector.ofValues [| series.Vector |]
        let df2 = Frame(series.Index, Index.ofKeys [column], data)
        df.Join(df2, JoinKind.Outer))

  // Apply operation 'op' with 'series' on the right to all columns convertible to 'T
  static member inline private PointwiseFrameSeriesR<'T>(frame:Frame<'TRowKey, 'TColumnKey>, series:Series<'TRowKey, 'T>, op:'T -> 'T -> 'T) =
    frame.Columns |> Series.mapValues (fun os ->
      match os.TryAs<'T>() with
      | OptionalValue.Present s -> s.JoinInner(series) |> Series.mapValues (fun (v1, v2) -> op v1 v2) :> ISeries<_>
      | _ -> os :> ISeries<_>)
    |> Frame<'TRowKey, 'TColumnKey>.FromColumnsNonGeneric

  // Apply operation 'op' to all columns that exist in both frames and are convertible to 'T
  static member inline private PointwiseFrameFrame<'T>(frame1:Frame<'TRowKey, 'TColumnKey>, frame2:Frame<'TRowKey, 'TColumnKey>, op:'T -> 'T -> 'T) =
    let rowIndex, f1cmd, f2cmd = frame1.IndexBuilder.Union( (frame1.RowIndex, Vectors.Return 0), (frame2.RowIndex, Vectors.Return 1) )
    frame1.Columns.Join(frame2.Columns).Select(fun (KeyValue(_, (l, r))) ->
      let (|TryGetAsT|_|) (lv:ObjectSeries<'TRowKey>) = lv.TryAs<'T>() |> OptionalValue.asOption
      match l, r with
      | OptionalValue.Present (TryGetAsT lv), OptionalValue.Present (TryGetAsT rv) ->
          let res = Vectors.Combine(f1cmd, f2cmd, VectorValueTransform.CreateLifted op)
          let newVector = frame1.VectorBuilder.Build(res, [| lv.Vector; rv.Vector |]) 
          Series<_, _>(rowIndex, newVector, frame1.VectorBuilder, frame1.IndexBuilder) :> ISeries<_>
      | OptionalValue.Present v, _ -> 
          let newVector = frame1.VectorBuilder.Build(f1cmd, [| v.Vector |])
          Series<_, _>(rowIndex, newVector, frame1.VectorBuilder, frame1.IndexBuilder) :> ISeries<_>
      | _, OptionalValue.Present v -> 
          let newVector = frame2.VectorBuilder.Build(substitute (1, 0) f2cmd, [| v.Vector |])
          Series<_, _>(rowIndex, newVector, frame2.VectorBuilder, frame2.IndexBuilder) :> ISeries<_>
      | _ -> failwith "PointwiseFrameFrame: Join failed."  )      
    |> Frame<'TRowKey, 'TColumnKey>.FromColumnsNonGeneric

  // Apply operation 'op' with 'scalar' on the right to all columns convertible to 'T
  static member inline private ScalarOperationR<'T>(frame:Frame<'TRowKey, 'TColumnKey>, scalar:'T, op:'T -> 'T -> 'T) : Frame<'TRowKey, 'TColumnKey> =
    frame.Columns |> Series.mapValues (fun os -> 
      match os.TryAs<'T>() with
      | OptionalValue.Present s -> (Series.mapValues (fun v -> op v scalar) s) :> ISeries<_>
      | _ -> os :> ISeries<_>)
    |> Frame<'TRowKey, 'TColumnKey>.FromColumnsNonGeneric

  // Apply operation 'op' with 'series' on the left to all columns convertible to 'T
  static member inline private PointwiseFrameSeriesL<'T>(frame:Frame<'TRowKey, 'TColumnKey>, series:Series<'TRowKey, 'T>, op:'T -> 'T -> 'T) =
    Frame<'TRowKey, 'TColumnKey>.PointwiseFrameSeriesR<'T>(frame, series, fun a b -> op b a)
  // Apply operation 'op' with 'scalar' on the left to all columns convertible to 'T
  static member inline private ScalarOperationL<'T>(frame:Frame<'TRowKey, 'TColumnKey>, scalar:'T, op:'T -> 'T -> 'T) : Frame<'TRowKey, 'TColumnKey> =
    Frame<'TRowKey, 'TColumnKey>.ScalarOperationR<'T>(frame, scalar, fun a b -> op b a)

  // Pointwise binary operations applied to two frames

  /// [category:Operators]
  static member (+) (frame1:Frame<'TRowKey, 'TColumnKey>, frame2:Frame<'TRowKey, 'TColumnKey>) =
    Frame<'TRowKey, 'TColumnKey>.PointwiseFrameFrame<float>(frame1, frame2, (+))
  /// [category:Operators]
  static member (-) (frame1:Frame<'TRowKey, 'TColumnKey>, frame2:Frame<'TRowKey, 'TColumnKey>) =
    Frame<'TRowKey, 'TColumnKey>.PointwiseFrameFrame<float>(frame1, frame2, (-))
  /// [category:Operators]
  static member (*) (frame1:Frame<'TRowKey, 'TColumnKey>, frame2:Frame<'TRowKey, 'TColumnKey>) =
    Frame<'TRowKey, 'TColumnKey>.PointwiseFrameFrame<float>(frame1, frame2, (*))
  /// [category:Operators]
  static member (/) (frame1:Frame<'TRowKey, 'TColumnKey>, frame2:Frame<'TRowKey, 'TColumnKey>) =
    Frame<'TRowKey, 'TColumnKey>.PointwiseFrameFrame<float>(frame1, frame2, (/))

  // Binary operators taking float/int series on the left/right (int is converted to float)

  /// [category:Operators]
  static member (+) (frame:Frame<'TRowKey, 'TColumnKey>, series:Series<'TRowKey, float>) =
    Frame<'TRowKey, 'TColumnKey>.PointwiseFrameSeriesR<float>(frame, series, (+))
  /// [category:Operators]
  static member (-) (frame:Frame<'TRowKey, 'TColumnKey>, series:Series<'TRowKey, float>) =
    Frame<'TRowKey, 'TColumnKey>.PointwiseFrameSeriesR<float>(frame, series, (-))
  /// [category:Operators]
  static member (*) (frame:Frame<'TRowKey, 'TColumnKey>, series:Series<'TRowKey, float>) =
    Frame<'TRowKey, 'TColumnKey>.PointwiseFrameSeriesR<float>(frame, series, (*))
  /// [category:Operators]
  static member (/) (frame:Frame<'TRowKey, 'TColumnKey>, series:Series<'TRowKey, float>) =
    Frame<'TRowKey, 'TColumnKey>.PointwiseFrameSeriesR<float>(frame, series, (/))
  /// [category:Operators]
  static member (+) (series:Series<'TRowKey, float>, frame:Frame<'TRowKey, 'TColumnKey>) =
    Frame<'TRowKey, 'TColumnKey>.PointwiseFrameSeriesL<float>(frame, series, (+))
  /// [category:Operators]
  static member (-) (series:Series<'TRowKey, float>, frame:Frame<'TRowKey, 'TColumnKey>) =
    Frame<'TRowKey, 'TColumnKey>.PointwiseFrameSeriesL<float>(frame, series, (-))
  /// [category:Operators]
  static member (*) (series:Series<'TRowKey, float>, frame:Frame<'TRowKey, 'TColumnKey>) =
    Frame<'TRowKey, 'TColumnKey>.PointwiseFrameSeriesL<float>(frame, series, (*))
  /// [category:Operators]
  static member (/) (series:Series<'TRowKey, float>, frame:Frame<'TRowKey, 'TColumnKey>) =
    Frame<'TRowKey, 'TColumnKey>.PointwiseFrameSeriesL<float>(frame, series, (/))

  /// [category:Operators]
  static member (+) (frame:Frame<'TRowKey, 'TColumnKey>, series:Series<'TRowKey, int>) =
    Frame<'TRowKey, 'TColumnKey>.PointwiseFrameSeriesR<float>(frame, Series.mapValues float series, (+))
  /// [category:Operators]
  static member (-) (frame:Frame<'TRowKey, 'TColumnKey>, series:Series<'TRowKey, int>) =
    Frame<'TRowKey, 'TColumnKey>.PointwiseFrameSeriesR<float>(frame, Series.mapValues float series, (-))
  /// [category:Operators]
  static member (*) (frame:Frame<'TRowKey, 'TColumnKey>, series:Series<'TRowKey, int>) =
    Frame<'TRowKey, 'TColumnKey>.PointwiseFrameSeriesR<float>(frame, Series.mapValues float series, (*))
  /// [category:Operators]
  static member (/) (frame:Frame<'TRowKey, 'TColumnKey>, series:Series<'TRowKey, int>) =
    Frame<'TRowKey, 'TColumnKey>.PointwiseFrameSeriesR<float>(frame, Series.mapValues float series, (/))
  /// [category:Operators]
  static member (+) (series:Series<'TRowKey, int>, frame:Frame<'TRowKey, 'TColumnKey>) =
    Frame<'TRowKey, 'TColumnKey>.PointwiseFrameSeriesL<float>(frame, Series.mapValues float series, (+))
  /// [category:Operators]
  static member (-) (series:Series<'TRowKey, int>, frame:Frame<'TRowKey, 'TColumnKey>) =
    Frame<'TRowKey, 'TColumnKey>.PointwiseFrameSeriesL<float>(frame, Series.mapValues float series, (-))
  /// [category:Operators]
  static member (*) (series:Series<'TRowKey, int>, frame:Frame<'TRowKey, 'TColumnKey>) =
    Frame<'TRowKey, 'TColumnKey>.PointwiseFrameSeriesL<float>(frame, Series.mapValues float series, (*))
  /// [category:Operators]
  static member (/) (series:Series<'TRowKey, int>, frame:Frame<'TRowKey, 'TColumnKey>) =
    Frame<'TRowKey, 'TColumnKey>.PointwiseFrameSeriesL<float>(frame, Series.mapValues float series, (/))

    
  // Binary operators taking float/int scalar on the left/right (int is converted to float)

  /// [category:Operators]
  static member (+) (frame:Frame<'TRowKey, 'TColumnKey>, scalar:float) =
    Frame<'TRowKey, 'TColumnKey>.ScalarOperationR<float>(frame, scalar, (+))
  /// [category:Operators]
  static member (+) (scalar:float, frame:Frame<'TRowKey, 'TColumnKey>) =
    Frame<'TRowKey, 'TColumnKey>.ScalarOperationL<float>(frame, scalar, (+))
  /// [category:Operators]
  static member (-) (frame:Frame<'TRowKey, 'TColumnKey>, scalar:float) =
    Frame<'TRowKey, 'TColumnKey>.ScalarOperationR<float>(frame, scalar, (-))
  /// [category:Operators]
  static member (-) (scalar:float, frame:Frame<'TRowKey, 'TColumnKey>) =
    Frame<'TRowKey, 'TColumnKey>.ScalarOperationL<float>(frame, scalar, (-))
  /// [category:Operators]
  static member (*) (frame:Frame<'TRowKey, 'TColumnKey>, scalar:float) =
    Frame<'TRowKey, 'TColumnKey>.ScalarOperationR<float>(frame, scalar, (*))
  /// [category:Operators]
  static member (*) (scalar:float, frame:Frame<'TRowKey, 'TColumnKey>) =
    Frame<'TRowKey, 'TColumnKey>.ScalarOperationL<float>(frame, scalar, (*))
  /// [category:Operators]
  static member (/) (frame:Frame<'TRowKey, 'TColumnKey>, scalar:float) =
    Frame<'TRowKey, 'TColumnKey>.ScalarOperationR<float>(frame, scalar, (/))
  /// [category:Operators]
  static member (/) (scalar:float, frame:Frame<'TRowKey, 'TColumnKey>) =
    Frame<'TRowKey, 'TColumnKey>.ScalarOperationL<float>(frame, scalar, (/))

  /// [category:Operators]
  static member (+) (frame:Frame<'TRowKey, 'TColumnKey>, scalar:int) =
    Frame<'TRowKey, 'TColumnKey>.ScalarOperationR<float>(frame, float scalar, (+))
  /// [category:Operators]
  static member (+) (scalar:int, frame:Frame<'TRowKey, 'TColumnKey>) =
    Frame<'TRowKey, 'TColumnKey>.ScalarOperationL<float>(frame, float scalar, (+))
  /// [category:Operators]
  static member (-) (frame:Frame<'TRowKey, 'TColumnKey>, scalar:int) =
    Frame<'TRowKey, 'TColumnKey>.ScalarOperationR<float>(frame, float scalar, (-))
  /// [category:Operators]
  static member (-) (scalar:int, frame:Frame<'TRowKey, 'TColumnKey>) =
    Frame<'TRowKey, 'TColumnKey>.ScalarOperationL<float>(frame, float scalar, (-))
  /// [category:Operators]
  static member (*) (frame:Frame<'TRowKey, 'TColumnKey>, scalar:int) =
    Frame<'TRowKey, 'TColumnKey>.ScalarOperationR<float>(frame, float scalar, (*))
  /// [category:Operators]
  static member (*) (scalar:int, frame:Frame<'TRowKey, 'TColumnKey>) =
    Frame<'TRowKey, 'TColumnKey>.ScalarOperationL<float>(frame, float scalar, (*))
  /// [category:Operators]
  static member (/) (frame:Frame<'TRowKey, 'TColumnKey>, scalar:int) =
    Frame<'TRowKey, 'TColumnKey>.ScalarOperationR<float>(frame, float scalar, (/))
  /// [category:Operators]
  static member (/) (scalar:int, frame:Frame<'TRowKey, 'TColumnKey>) =
    Frame<'TRowKey, 'TColumnKey>.ScalarOperationL<float>(frame, float scalar, (/))

  // ----------------------------------------------------------------------------------------------
  // Indexing
  // ----------------------------------------------------------------------------------------------

  /// [category:Indexing]
  member frame.RealignRows(keys) = 
    // Create empty frame with the required keys    
    let empty = Frame<_, _>(frame.IndexBuilder.Create(keys, None), frame.IndexBuilder.Create([], None), frame.VectorBuilder.Create [||])
    for key, series in frame.Columns |> Series.observations do 
      empty.AddSeries(key, series)
    empty

  /// [category:Indexing]
  member frame.IndexRowsWith<'TNewRowIndex when 'TNewRowIndex : equality>(keys:seq<'TNewRowIndex>) =
    let newRowIndex = frame.IndexBuilder.Create(keys, None)
    let getRange = VectorHelpers.getVectorRange frame.VectorBuilder frame.RowIndex.Range
    let newData = frame.Data.Select(getRange)
    Frame<_, _>(newRowIndex, frame.ColumnIndex, newData)


  // ----------------------------------------------------------------------------------------------
  // Constructor
  // ----------------------------------------------------------------------------------------------

  new(names:seq<'TColumnKey>, columns:seq<ISeries<'TRowKey>>) =
    let df = Frame(Index.ofKeys [], Index.ofKeys [], Vector.ofValues [])
    let df = (df, Seq.zip names columns) ||> Seq.fold (fun df (colKey, colData) ->
      let other = Frame(colData.Index, Index.ofUnorderedKeys [colKey], Vector.ofValues [colData.Vector])
      df.Join(other, JoinKind.Outer) )
    Frame(df.RowIndex, df.ColumnIndex, df.Data)

  member frame.Clone() =
    Frame<_, _>(rowIndex, columnIndex, data)

  // ----------------------------------------------------------------------------------------------
  // Interfaces and overrides
  // ----------------------------------------------------------------------------------------------

  override frame.Equals(another) = 
    match another with
    | null -> false
    | :? Frame<'TRowKey, 'TColumnKey> as another -> 
        frame.RowIndex.Equals(another.RowIndex) &&
        frame.ColumnIndex.Equals(another.ColumnIndex) &&
        frame.Data.Equals(another.Data) 
    | _ -> false

  override frame.GetHashCode() =
    let (++) h1 h2 = ((h1 <<< 5) + h1) ^^^ h2
    frame.RowIndex.GetHashCode() ++ frame.ColumnIndex.GetHashCode() ++ frame.Data.GetHashCode()

  interface IFsiFormattable with
    member frame.Format() = 
      try
        let colLevels = 
          match frame.ColumnIndex.Keys |> Seq.headOrNone with 
          Some colKey -> CustomKey.Get(colKey).Levels | _ -> 1
        let rowLevels = 
          match frame.RowIndex.Keys |> Seq.headOrNone with 
          Some rowKey -> CustomKey.Get(rowKey).Levels | _ -> 1

        let getLevel ordered previous reset maxLevel level (key:'K) = 
          let levelKey = 
            if level = 0 && maxLevel = 0 then box key
            else CustomKey.Get(key).GetLevel(level)
          if ordered && (Some levelKey = !previous) then "" 
          else previous := Some levelKey; reset(); levelKey.ToString()
        
        seq { 
          // Yield headers (for all column levels)
          for colLevel in 0 .. colLevels - 1 do 
            yield [
              // Prefix with appropriate number of (empty) row keys
              for i in 0 .. rowLevels - 1 do yield "" 
              yield ""
              let previous = ref None
              for colKey, _ in frame.ColumnIndex.Mappings do 
                yield getLevel frame.ColumnIndex.Ordered previous ignore colLevels colLevel colKey ]

          // Yield row data
          let rows = frame.Rows
          let previous = Array.init rowLevels (fun _ -> ref None)
          let reset i () = for j in i + 1 .. rowLevels - 1 do previous.[j] := None
          for item in frame.RowIndex.Mappings |> Seq.startAndEnd Formatting.StartItemCount Formatting.EndItemCount do
            match item with 
            | Choice2Of3() ->
                yield [
                  // Prefix with appropriate number of (empty) row keys
                  for i in 0 .. rowLevels - 1 do yield if i = 0 then ":" else ""
                  yield ""
                  for i in 1 .. data.DataSequence |> Seq.length -> "..." ]
            | Choice1Of3(rowKey, addr) | Choice3Of3(rowKey, addr) ->
                let row = rows.[rowKey]
                yield [
                  // Yield all row keys
                  for rowLevel in 0 .. rowLevels - 1 do 
                    yield getLevel frame.RowIndex.Ordered previous.[rowLevel] (reset rowLevel) rowLevels rowLevel rowKey
                  yield "->"
                  for KeyValue(_, value) in SeriesExtensions.GetAllObservations(row) do  // TODO: is this good?
                    yield value.ToString() ] }
        |> array2D
        |> Formatting.formatTable
      with e -> sprintf "Formatting failed: %A" e

// ------------------------------------------------------------------------------------------------
// Building frame from series of rows/columns (this has to be here, because we need it in 
// ColumnSeries/RowSeries (below) which are referenced by df.Rows, df.Columns (above)
// ------------------------------------------------------------------------------------------------

and FrameUtils = 
  // Current vector builder to be used for creating frames
  static member vectorBuilder = Vectors.ArrayVector.ArrayVectorBuilder.Instance 
  // Current index builder to be used for creating frames
  static member indexBuilder = Indices.Linear.LinearIndexBuilder.Instance

  /// Create data frame containing a single column
  static member createColumn<'TColumnKey, 'TRowKey when 'TColumnKey : equality and 'TRowKey : equality>
      (column:'TColumnKey, series:ISeries<'TRowKey>) = 
    let data = Vector.ofValues [| series.Vector |]
    Frame(series.Index, Index.ofKeys [column], data)

  /// Create data frame containing a single row
  static member createRow(row:'TRowKey, series:Series<'TColumnKey, 'TValue>) = 
    let data = series.Vector.SelectMissing(fun v -> 
      let res = Vectors.ArrayVector.ArrayVectorBuilder.Instance.CreateMissing [| v |] 
      OptionalValue(res :> IVector))
    Frame(Index.ofKeys [row], series.Index, data)

  /// Create data frame from a series of rows
  static member fromRows<'TRowKey, 'TColumnKey, 'TSeries
        when 'TRowKey : equality and 'TColumnKey : equality and 'TSeries :> ISeries<'TColumnKey>>
      (nested:Series<'TRowKey, 'TSeries>) =

    // Union column indices, ignoring the vector trasnformations
    let columnIndex = nested.Values |> Seq.map (fun sr -> sr.Index) |> Seq.reduce (fun i1 i2 -> 
      let index, _, _ = FrameUtils.indexBuilder.Union( (i1, Vectors.Return 0), (i2, Vectors.Return 0) )
      index )
    // Row index is just the index of the series
    let rowIndex = nested.Index

    // Dispatcher that creates column vector of the right type
    let columnCreator key =
      { new VectorHelpers.ValueCallSite1<IVector> with
          override x.Invoke<'T>(_:'T) = 
            let it = nested.SelectOptional(fun kvp ->
              if kvp.Value.HasValue then 
                kvp.Value.Value.TryGetObject(key) 
                |> OptionalValue.map (fun v -> System.Convert.ChangeType(v, typeof<'T>) |> unbox<'T>)
              else OptionalValue.Missing)
            it.Vector :> IVector }
      |> VectorHelpers.createValueDispatcher
    // Create data vectors
    let data = 
      columnIndex.Keys 
      |> Seq.map (fun key ->
          // Pick a witness from the column, so that we can use column creator
          // and try creating a typed IVector based on the column type
          try
            let someValue =
              nested |> Series.observations |> Seq.tryPick (fun (_, v) -> 
                v.TryGetObject(key) |> OptionalValue.asOption)
            let someValue = defaultArg someValue (obj())
            columnCreator key someValue
          with :? System.InvalidCastException ->
            // If that failes, the sequence is heterogeneous
            // so we try again and pass object as a witness
            columnCreator key (obj()) )
      |> Array.ofSeq |> FrameUtils.vectorBuilder.Create
    Frame(rowIndex, columnIndex, data)

  /// Create data frame from a series of columns
  static member fromColumns<'TRowKey, 'TColumnKey, 'TSeries when 'TSeries :> ISeries<'TRowKey> 
        and 'TRowKey : equality and 'TColumnKey : equality>
      (nested:Series<'TColumnKey, 'TSeries>) =
    let initial = Frame(Index.ofKeys [], Index.ofUnorderedKeys [], Vector.ofValues [| |])
    (initial, Series.observations nested) ||> Seq.fold (fun df (name, series) -> 
      df.Join(FrameUtils.createColumn(name, series), JoinKind.Outer))

// ------------------------------------------------------------------------------------------------
// 
// ------------------------------------------------------------------------------------------------

and ColumnSeries<'TRowKey, 'TColumnKey when 'TRowKey : equality and 'TColumnKey : equality>(index, vector, vectorBuilder, indexBuilder) =
  inherit Series<'TColumnKey, ObjectSeries<'TRowKey>>(index, vector, vectorBuilder, indexBuilder)
  new(series:Series<'TColumnKey, ObjectSeries<'TRowKey>>) = 
    ColumnSeries(series.Index, series.Vector, series.VectorBuilder, series.IndexBuilder)

  [<EditorBrowsable(EditorBrowsableState.Never)>]
  member x.GetSlice(lo, hi) = base.GetSlice(lo, hi) |> FrameUtils.fromColumns
  [<EditorBrowsable(EditorBrowsableState.Never)>]
  member x.GetByLevel(level) = base.GetByLevel(level) |> FrameUtils.fromColumns
  member x.Item with get(items) = x.GetItems(items) |> FrameUtils.fromColumns
  member x.Item with get(level) = x.GetByLevel(level)

and RowSeries<'TRowKey, 'TColumnKey when 'TRowKey : equality and 'TColumnKey : equality>(index, vector, vectorBuilder, indexBuilder) =
  inherit Series<'TRowKey, ObjectSeries<'TColumnKey>>(index, vector, vectorBuilder, indexBuilder)
  new(series:Series<'TRowKey, ObjectSeries<'TColumnKey>>) = 
    RowSeries(series.Index, series.Vector, series.VectorBuilder, series.IndexBuilder)

  [<EditorBrowsable(EditorBrowsableState.Never)>]
  member x.GetSlice(lo, hi) = base.GetSlice(lo, hi) |> FrameUtils.fromRows
  [<EditorBrowsable(EditorBrowsableState.Never)>]
  member x.GetByLevel(level) = base.GetByLevel(level) |> FrameUtils.fromRows
  member x.Item with get(items) = x.GetItems(items) |> FrameUtils.fromRows
  member x.Item with get(level) = x.GetByLevel(level)