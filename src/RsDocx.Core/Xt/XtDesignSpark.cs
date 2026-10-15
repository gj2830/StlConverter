namespace RsDocx.Core.Xt;

/// <summary>
/// What DesignSpark (SpaceClaim) writes in every geometry part, whatever is in it: the header, its embedded schema
/// (the node types it stores differently from the Parasolid base schema) and the definition of the SDL/TYSA_NAME
/// attribute that carries the document.xml monikers. Taken once from a DesignSpark file (generated, do not edit).
/// </summary>
public static class XtDesignSpark
{
    public const char Flag = 'B';
    public const string ModellerVersion = ": TRANSMIT FILE created by modeller version 3401212";
    public const string SchemaVersion = "SCH_3401212_34101_13006";
    public const short MaxNodeTypes = 231;

    /// <summary>The embedded schema entries, per node type.</summary>
    public static readonly IReadOnlyList<XtSchemaEntry> Schemas =
    [
        new() { NodeType = 12, Kind = XtSchemaKind.Edited, Name = null, Description = null,
            Fields = [new("highest_node_id", 0, 0, "d"), new("attributes_groups", 0, 0, "p"), new("attribute_chains", 0, 0, "p"), new("lattice", 222, 0, "p"), new("surface", 0, 0, "p"), new("curve", 0, 0, "p"), new("point", 0, 0, "p"), new("mesh", 1006, 0, "p"), new("polyline", 1008, 0, "p"), new("key", 0, 0, "p"), new("res_size", 0, 0, "f"), new("res_linear", 0, 0, "f"), new("ref_instance", 0, 0, "p"), new("next", 0, 0, "p"), new("previous", 0, 0, "p"), new("state", 0, 0, "u"), new("owner", 1040, 0, "p"), new("body_type", 0, 0, "u"), new("nom_geom_state", 0, 0, "u"), new("shell", 0, 0, "p"), new("boundary_lattice", 222, 0, "p"), new("boundary_surface", 0, 0, "p"), new("boundary_curve", 0, 0, "p"), new("boundary_point", 0, 0, "p"), new("boundary_mesh", 1006, 0, "p"), new("boundary_polyline", 1008, 0, "p"), new("region", 0, 0, "p"), new("edge", 0, 0, "p"), new("vertex", 0, 0, "p"), new("index_map_offset", 0, 0, "d"), new("index_map", 82, 0, "p"), new("node_id_index_map", 82, 0, "p"), new("schema_embedding_map", 82, 0, "p"), new("child", 12, 0, "p"), new("lowest_node_id", 0, 0, "d"), new("mesh_offset_data", 206, 0, "p")],
            Ops = [new('C'), new('C'), new('C'), new('I', new("lattice", 222, 0, "p")), new('C'), new('C'), new('C'), new('I', new("mesh", 1006, 0, "p")), new('I', new("polyline", 1008, 0, "p")), new('C'), new('C'), new('C'), new('C'), new('C'), new('C'), new('C'), new('D'), new('I', new("owner", 1040, 0, "p")), new('C'), new('C'), new('C'), new('I', new("boundary_lattice", 222, 0, "p")), new('C'), new('C'), new('C'), new('I', new("boundary_mesh", 1006, 0, "p")), new('I', new("boundary_polyline", 1008, 0, "p")), new('C'), new('C'), new('C'), new('A', new("index_map_offset", 0, 0, "d")), new('A', new("index_map", 82, 0, "p")), new('A', new("node_id_index_map", 82, 0, "p")), new('A', new("schema_embedding_map", 82, 0, "p")), new('A', new("child", 12, 0, "p")), new('A', new("lowest_node_id", 0, 0, "d")), new('A', new("mesh_offset_data", 206, 0, "p"))],
        },
        new() { NodeType = 13, Kind = XtSchemaKind.SameAsBase, Name = null, Description = null,
            Fields = [new("node_id", 0, 0, "d"), new("attributes_groups", 0, 0, "p"), new("body", 0, 0, "p"), new("next", 0, 0, "p"), new("face", 0, 0, "p"), new("edge", 0, 0, "p"), new("vertex", 0, 0, "p"), new("region", 0, 0, "p"), new("front_face", 0, 0, "p")],
        },
        new() { NodeType = 14, Kind = XtSchemaKind.SameAsBase, Name = null, Description = null,
            Fields = [new("node_id", 0, 0, "d"), new("attributes_groups", 0, 0, "p"), new("tolerance", 0, 0, "f"), new("next", 0, 0, "p"), new("previous", 0, 0, "p"), new("loop", 0, 0, "p"), new("shell", 0, 0, "p"), new("surface", 0, 0, "p"), new("sense", 0, 0, "c"), new("next_on_surface", 0, 0, "p"), new("previous_on_surface", 0, 0, "p"), new("next_front", 0, 0, "p"), new("previous_front", 0, 0, "p"), new("front_shell", 0, 0, "p")],
        },
        new() { NodeType = 15, Kind = XtSchemaKind.SameAsBase, Name = null, Description = null,
            Fields = [new("node_id", 0, 0, "d"), new("attributes_groups", 0, 0, "p"), new("fin", 0, 0, "p"), new("face", 0, 0, "p"), new("next", 0, 0, "p")],
        },
        new() { NodeType = 16, Kind = XtSchemaKind.SameAsBase, Name = null, Description = null,
            Fields = [new("node_id", 0, 0, "d"), new("attributes_groups", 0, 0, "p"), new("tolerance", 0, 0, "f"), new("fin", 0, 0, "p"), new("previous", 0, 0, "p"), new("next", 0, 0, "p"), new("curve", 0, 0, "p"), new("next_on_curve", 0, 0, "p"), new("previous_on_curve", 0, 0, "p"), new("owner", 0, 0, "p")],
        },
        new() { NodeType = 17, Kind = XtSchemaKind.SameAsBase, Name = null, Description = null,
            Fields = [new("attributes_groups", 0, 0, "p"), new("loop", 0, 0, "p"), new("forward", 0, 0, "p"), new("backward", 0, 0, "p"), new("vertex", 0, 0, "p"), new("other", 0, 0, "p"), new("edge", 0, 0, "p"), new("curve", 0, 0, "p"), new("next_at_vx", 0, 0, "p"), new("sense", 0, 0, "c")],
        },
        new() { NodeType = 18, Kind = XtSchemaKind.SameAsBase, Name = null, Description = null,
            Fields = [new("node_id", 0, 0, "d"), new("attributes_groups", 0, 0, "p"), new("fin", 0, 0, "p"), new("previous", 0, 0, "p"), new("next", 0, 0, "p"), new("point", 0, 0, "p"), new("tolerance", 0, 0, "f"), new("owner", 0, 0, "p")],
        },
        new() { NodeType = 19, Kind = XtSchemaKind.Edited, Name = null, Description = null,
            Fields = [new("node_id", 0, 0, "d"), new("attributes_groups", 0, 0, "p"), new("body", 0, 0, "p"), new("next", 0, 0, "p"), new("previous", 0, 0, "p"), new("shell", 0, 0, "p"), new("frame", 230, 0, "p"), new("type", 0, 0, "c"), new("owner", 12, 0, "p")],
            Ops = [new('C'), new('C'), new('C'), new('C'), new('C'), new('C'), new('I', new("frame", 230, 0, "p")), new('C'), new('A', new("owner", 12, 0, "p"))],
        },
        new() { NodeType = 29, Kind = XtSchemaKind.SameAsBase, Name = null, Description = null,
            Fields = [new("node_id", 0, 0, "d"), new("attributes_groups", 0, 0, "p"), new("owner", 0, 0, "p"), new("next", 0, 0, "p"), new("previous", 0, 0, "p"), new("pvec", 0, 0, "v")],
        },
        new() { NodeType = 30, Kind = XtSchemaKind.SameAsBase, Name = null, Description = null,
            Fields = [new("node_id", 0, 0, "d"), new("attributes_groups", 0, 0, "p"), new("owner", 0, 0, "p"), new("next", 0, 0, "p"), new("previous", 0, 0, "p"), new("geometric_owner", 0, 0, "p"), new("sense", 0, 0, "c"), new("pvec", 0, 0, "v"), new("direction", 0, 0, "v")],
        },
        new() { NodeType = 50, Kind = XtSchemaKind.SameAsBase, Name = null, Description = null,
            Fields = [new("node_id", 0, 0, "d"), new("attributes_groups", 0, 0, "p"), new("owner", 0, 0, "p"), new("next", 0, 0, "p"), new("previous", 0, 0, "p"), new("geometric_owner", 0, 0, "p"), new("sense", 0, 0, "c"), new("pvec", 0, 0, "v"), new("normal", 0, 0, "v"), new("x_axis", 0, 0, "v")],
        },
        new() { NodeType = 70, Kind = XtSchemaKind.Edited, Name = null, Description = null,
            Fields = [new("node_id", 0, 0, "d"), new("list_type", 0, 0, "u"), new("notransmit", 0, 0, "l"), new("owner", 0, 0, "p"), new("next", 0, 0, "p"), new("previous", 0, 0, "p"), new("list_length", 0, 0, "d"), new("block_length", 0, 0, "d"), new("finger_index", 0, 0, "d"), new("finger_block", 1012, 0, "p"), new("list_block", 0, 0, "p")],
            Ops = [new('C'), new('I', new("list_type", 0, 0, "u")), new('I', new("notransmit", 0, 0, "l")), new('C'), new('C'), new('C'), new('D'), new('C'), new('C'), new('D'), new('I', new("finger_index", 0, 0, "d")), new('I', new("finger_block", 1012, 0, "p")), new('C')],
        },
        new() { NodeType = 74, Kind = XtSchemaKind.Edited, Name = null, Description = null,
            Fields = [new("n_entries", 0, 0, "d"), new("index_map_offset", 0, 0, "d"), new("next_block", 0, 0, "p"), new("entries", 0, 1, "p")],
            Ops = [new('C'), new('I', new("index_map_offset", 0, 0, "d")), new('C'), new('C')],
        },
        new() { NodeType = 79, Kind = XtSchemaKind.SameAsBase, Name = null, Description = null,
            Fields = [new("string", 0, 1, "c")],
        },
        new() { NodeType = 80, Kind = XtSchemaKind.Edited, Name = null, Description = null,
            Fields = [new("next", 0, 0, "p"), new("identifier", 0, 0, "p"), new("type_id", 0, 0, "d"), new("actions", 0, 8, "u"), new("field_names", 0, 0, "p"), new("legal_owners", 0, 16, "l"), new("fields", 0, 1, "u")],
            Ops = [new('C'), new('C'), new('C'), new('C'), new('C'), new('D'), new('I', new("legal_owners", 0, 16, "l")), new('C')],
        },
        new() { NodeType = 81, Kind = XtSchemaKind.SameAsBase, Name = null, Description = null,
            Fields = [new("node_id", 0, 0, "d"), new("definition", 0, 0, "p"), new("owner", 0, 0, "p"), new("next", 0, 0, "p"), new("previous", 0, 0, "p"), new("next_of_type", 0, 0, "p"), new("previous_of_type", 0, 0, "p"), new("fields", 0, 1, "p")],
        },
        new() { NodeType = 84, Kind = XtSchemaKind.SameAsBase, Name = null, Description = null,
            Fields = [new("values", 0, 1, "c")],
        },
        new() { NodeType = 176, Kind = XtSchemaKind.New, Name = "PART_XMT_BLOCK", Description = "Part list",
            Fields = [new("n_entries", 0, 0, "d"), new("index_map_offset", 0, 0, "d"), new("index_map", 82, 0, "p"), new("schema_embedding_map", 82, 0, "p"), new("mesh_offset_data", 206, 0, "p"), new("entries", 1005, 1, "p")],
        },
    ];

    /// <summary>The SDL/TYSA_NAME attribute definition: its field values (pointers left to the builder) and the length of
    /// its variable field.</summary>
    public static object[] NameAttribDefValues() => new object[] { 134, 135, 8017, new object[] { (byte)0, (byte)0, (byte)0, (byte)0, (byte)0, (byte)0, (byte)0, (byte)0 }, 0, new object[] { (byte)1, (byte)1, (byte)1, (byte)1, (byte)1, (byte)1, (byte)1, (byte)1, (byte)1, (byte)1, (byte)1, (byte)1, (byte)1, (byte)0, (byte)1, (byte)0 }, new object[] { (byte)3 } };
    public const int NameAttribDefLength = 1;
}
