namespace GuiLabs.FileFormat.PE.Dotnet;

public class PdbStream : MetadataStream
{
    public TableInfo[] TableInfos = new TableInfo[64];

    public override void Parse()
    {
        PdbGuid = Add<SixteenBytes>("PDB Guid");
        PdbAge = AddFourBytes("PDB Age");
        EntrypointToken = AddFourBytes("Entrypoint Token");
        ExternalTablesMask = AddEightBytes("External tables mask");
        TableLengths = Add<Sequence>("Table lengths");

        ulong mask = ExternalTablesMask.ReadUInt64();
        for (int i = 0; i < 64; i++)
        {
            if ((mask & (1UL << i)) == 0)
            {
                continue;
            }

            var tableLength = TableLengths.AddFourBytes();
            TableInfos[i].RowCount = tableLength.Value;
            tableLength.Text = $"Table {i} length: {tableLength.Value}";
        }
    }

    public SixteenBytes PdbGuid { get; set; }
    public FourBytes PdbAge { get; set; }
    public FourBytes EntrypointToken { get; set; }
    public EightBytes ExternalTablesMask { get; set; }
    public Sequence TableLengths { get; set; }
}