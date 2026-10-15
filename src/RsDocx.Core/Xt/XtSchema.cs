namespace RsDocx.Core.Xt;

/// <summary>
/// One field of a node type, as described in the XT schema / embedded schema.
/// Type is the XT field type letter: d (int32), p (pointer index), u/c/l (byte), n/w (int16),
/// f (double), v/h (3 doubles), i (2 doubles), b (6 doubles).
/// NElts: 0 = scalar, 1 = variable length, n &gt; 1 = fixed array of n.
/// </summary>
public sealed record XtFieldDef(string Name, short PtrClass, int NElts, string Type, bool Xmt = true)
{
    public bool IsPointer => Type == "p";
    public bool IsVariable => NElts == 1;

    public static XtFieldDef Ptr(string name, short ptrClass = 0, int nElts = 0) => new(name, ptrClass, nElts, "p");
    public static XtFieldDef Val(string name, string type, int nElts = 0) => new(name, 0, nElts, type);
}

public sealed record XtNodeTypeDef(int NodeType, string Name, IReadOnlyList<XtFieldDef> Fields)
{
    public bool IsVariable => Fields.Any(f => f.IsVariable);
}

/// <summary>
/// Reconstruction of the base schema SCH_13006 for the node types we have encountered.
/// Source: Parasolid XT Format Reference (Oct 2006, schema 12006) and verified against the
/// embedded-schema edit sequences in testdata/kubus.rsdocx (the reader must consume the file exactly).
/// Pointer classes of base fields are unknown/irrelevant here: edit sequences are replayed, never recomputed.
/// </summary>
public static class XtBaseSchema
{
    private static XtFieldDef P(string n, int ne = 0) => XtFieldDef.Ptr(n, 0, ne);
    private static XtFieldDef V(string n, string t, int ne = 0) => XtFieldDef.Val(n, t, ne);

    public static readonly IReadOnlyDictionary<int, XtNodeTypeDef> Types = new Dictionary<int, XtNodeTypeDef>
    {
        [12] = new(12, "BODY", [
            V("highest_node_id", "d"), P("attributes_groups"), P("attribute_chains"), P("surface"), P("curve"), P("point"),
            P("key"), V("res_size", "f"), V("res_linear", "f"), P("ref_instance"), P("next"), P("previous"), V("state", "u"),
            P("owner"), V("body_type", "u"), V("nom_geom_state", "u"), P("shell"), P("boundary_surface"), P("boundary_curve"),
            P("boundary_point"), P("region"), P("edge"), P("vertex")]),
        [13] = new(13, "SHELL", [
            V("node_id", "d"), P("attributes_groups"), P("body"), P("next"), P("face"), P("edge"), P("vertex"), P("region"),
            P("front_face")]),
        [14] = new(14, "FACE", [
            V("node_id", "d"), P("attributes_groups"), V("tolerance", "f"), P("next"), P("previous"), P("loop"), P("shell"),
            P("surface"), V("sense", "c"), P("next_on_surface"), P("previous_on_surface"), P("next_front"),
            P("previous_front"), P("front_shell")]),
        [15] = new(15, "LOOP", [V("node_id", "d"), P("attributes_groups"), P("fin"), P("face"), P("next")]),
        [16] = new(16, "EDGE", [
            V("node_id", "d"), P("attributes_groups"), V("tolerance", "f"), P("fin"), P("previous"), P("next"), P("curve"),
            P("next_on_curve"), P("previous_on_curve"), P("owner")]),
        [17] = new(17, "FIN", [
            P("attributes_groups"), P("loop"), P("forward"), P("backward"), P("vertex"), P("other"), P("edge"), P("curve"),
            P("next_at_vx"), V("sense", "c")]),
        [18] = new(18, "VERTEX", [
            V("node_id", "d"), P("attributes_groups"), P("fin"), P("previous"), P("next"), P("point"), V("tolerance", "f"),
            P("owner")]),
        [19] = new(19, "REGION", [
            V("node_id", "d"), P("attributes_groups"), P("body"), P("next"), P("previous"), P("shell"), V("type", "c")]),
        [29] = new(29, "POINT", [
            V("node_id", "d"), P("attributes_groups"), P("owner"), P("next"), P("previous"), V("pvec", "v")]),
        [30] = new(30, "LINE", [
            V("node_id", "d"), P("attributes_groups"), P("owner"), P("next"), P("previous"), P("geometric_owner"),
            V("sense", "c"), V("pvec", "v"), V("direction", "v")]),
        [31] = new(31, "CIRCLE", [
            V("node_id", "d"), P("attributes_groups"), P("owner"), P("next"), P("previous"), P("geometric_owner"),
            V("sense", "c"), V("centre", "v"), V("normal", "v"), V("x_axis", "v"), V("radius", "f")]),
        // 2006 spec omits 'sense'; V35 spec lists $c after geometric_owner (like CIRCLE). Verified by exact parse of sleutelrek.rsdocx.
        [32] = new(32, "ELLIPSE", [
            V("node_id", "d"), P("attributes_groups"), P("owner"), P("next"), P("previous"), P("geometric_owner"),
            V("sense", "c"), V("centre", "v"), V("normal", "v"), V("x_axis", "v"), V("major_radius", "f"),
            V("minor_radius", "f")]),
        [38] = new(38, "INTERSECTION", [
            V("node_id", "d"), P("attributes_groups"), P("owner"), P("next"), P("previous"), P("geometric_owner"),
            V("sense", "c"), P("surface", 2), P("chart"), P("start"), P("end")]),
        [40] = new(40, "CHART", [
            V("base_parameter", "f"), V("base_scale", "f"), V("chart_count", "d"), V("chordal_error", "f"),
            V("angular_error", "f"), V("parameter_error", "f", 2), V("hvec", "h", 1)]),
        [41] = new(41, "LIMIT", [V("type", "c"), V("hvec", "h", 1)]),
        [51] = new(51, "CYLINDER", [
            V("node_id", "d"), P("attributes_groups"), P("owner"), P("next"), P("previous"), P("geometric_owner"),
            V("sense", "c"), V("pvec", "v"), V("axis", "v"), V("radius", "f"), V("x_axis", "v")]),
        [52] = new(52, "CONE", [
            V("node_id", "d"), P("attributes_groups"), P("owner"), P("next"), P("previous"), P("geometric_owner"),
            V("sense", "c"), V("pvec", "v"), V("axis", "v"), V("radius", "f"), V("sin_half_angle", "f"),
            V("cos_half_angle", "f"), V("x_axis", "v")]),
        [53] = new(53, "SPHERE", [
            V("node_id", "d"), P("attributes_groups"), P("owner"), P("next"), P("previous"), P("geometric_owner"),
            V("sense", "c"), V("centre", "v"), V("radius", "f"), V("axis", "v"), V("x_axis", "v")]),
        [54] = new(54, "TORUS", [
            V("node_id", "d"), P("attributes_groups"), P("owner"), P("next"), P("previous"), P("geometric_owner"),
            V("sense", "c"), V("centre", "v"), V("axis", "v"), V("major_radius", "f"), V("minor_radius", "f"),
            V("x_axis", "v")]),
        [50] = new(50, "PLANE", [
            V("node_id", "d"), P("attributes_groups"), P("owner"), P("next"), P("previous"), P("geometric_owner"),
            V("sense", "c"), V("pvec", "v"), V("normal", "v"), V("x_axis", "v")]),
        [70] = new(70, "LIST", [
            V("node_id", "d"), P("owner"), P("next"), P("previous"), V("list_type", "d"), V("list_length", "d"),
            V("block_length", "d"), V("size_of_entry", "d"), P("list_block"), P("finger_block"), V("finger_index", "d"),
            V("notransmit", "l")]),
        [74] = new(74, "POINTER_LIS_BLOCK", [V("n_entries", "d"), P("next_block"), P("entries", 1)]),
        [79] = new(79, "ATT_DEF_ID", [V("string", "c", 1)]),
        [80] = new(80, "ATTRIB_DEF", [
            P("next"), P("identifier"), V("type_id", "d"), V("actions", "u", 8), P("field_names"),
            V("legal_owners", "l", 14), V("fields", "u", 1)]),
        [81] = new(81, "ATTRIBUTE", [
            V("node_id", "d"), P("definition"), P("owner"), P("next"), P("previous"), P("next_of_type"),
            P("previous_of_type"), P("fields", 1)]),
        [82] = new(82, "INT_VALUES", [V("values", "d", 1)]),
        [83] = new(83, "REAL_VALUES", [V("values", "f", 1)]),
        [84] = new(84, "CHAR_VALUES", [V("values", "c", 1)]),
        [98] = new(98, "UNICODE_VALUES", [V("values", "w", 1)]),

        // NURBS family (node type numbers from the V35 table; field order from the $-codes of the 2006 structs)
        // Blend / derived surfaces (2006 structs; names and $-codes extracted together, counts match)
        [56] = new(56, "BLENDED_EDGE", [
            V("node_id", "d"), P("attributes_groups"), P("owner"), P("next"), P("previous"), P("geometric_owner"),
            V("sense", "c"), V("blend_type", "c"), P("surface", 2), P("spine"), V("range", "f", 2),
            V("thumb_weight", "f", 2), P("boundary", 2), P("start"), P("end")]),
        [59] = new(59, "BLEND_BOUND", [
            V("node_id", "d"), P("attributes_groups"), P("owner"), P("next"), P("previous"), P("geometric_owner"),
            V("sense", "c"), V("boundary", "n"), P("blend")]),
        [60] = new(60, "OFFSET_SURF", [
            V("node_id", "d"), P("attributes_groups"), P("owner"), P("next"), P("previous"), P("geometric_owner"),
            V("sense", "c"), V("check", "c"), V("true_offset", "l"), P("surface"), V("offset", "f"), V("scale", "f")]),
        [67] = new(67, "SWEPT_SURF", [
            V("node_id", "d"), P("attributes_groups"), P("owner"), P("next"), P("previous"), P("geometric_owner"),
            V("sense", "c"), P("section"), V("sweep", "v"), V("scale", "f")]),
        [68] = new(68, "SPUN_SURF", [
            V("node_id", "d"), P("attributes_groups"), P("owner"), P("next"), P("previous"), P("geometric_owner"),
            V("sense", "c"), P("profile"), V("base", "v"), V("axis", "v"), V("start", "v"), V("end", "v"),
            V("start_param", "f"), V("end_param", "f"), V("x_axis", "v"), V("scale", "f")]),
        [141] = new(141, "GEOMETRIC_OWNER", [P("owner"), P("next"), P("previous"), P("shared_geometry")]),
        [45] = new(45, "BSPLINE_VERTICES", [V("vertices", "f", 1)]),
        [127] = new(127, "KNOT_MULT", [V("mult", "n", 1)]),
        [128] = new(128, "KNOT_SET", [V("knots", "f", 1)]),
        [134] = new(134, "B_CURVE", [
            V("node_id", "d"), P("attributes_groups"), P("owner"), P("next"), P("previous"), P("geometric_owner"),
            V("sense", "c"), P("nurbs"), P("data")]),
        [135] = new(135, "CURVE_DATA", [V("self_int", "u"), P("analytic_form")]),
        [136] = new(136, "NURBS_CURVE", [
            V("degree", "n"), V("n_vertices", "d"), V("vertex_dim", "n"), V("n_knots", "d"), V("knot_type", "u"),
            V("periodic", "l"), V("closed", "l"), V("rational", "l"), V("curve_form", "u"), P("bspline_vertices"),
            P("knot_mult"), P("knots")]),
        [133] = new(133, "TRIMMED_CURVE", [
            V("node_id", "d"), P("attributes_groups"), P("owner"), P("next"), P("previous"), P("geometric_owner"),
            V("sense", "c"), P("basis_curve"), V("point_1", "v"), V("point_2", "v"), V("parm_1", "f"), V("parm_2", "f")]),
        [137] = new(137, "SP_CURVE", [
            V("node_id", "d"), P("attributes_groups"), P("owner"), P("next"), P("previous"), P("geometric_owner"),
            V("sense", "c"), P("surface"), P("b_curve"), P("original"), V("tolerance_to_original", "f")]),
        [124] = new(124, "B_SURFACE", [
            V("node_id", "d"), P("attributes_groups"), P("owner"), P("next"), P("previous"), P("geometric_owner"),
            V("sense", "c"), P("nurbs"), P("data")]),
        [125] = new(125, "SURFACE_DATA", [
            V("original_uint", "i"), V("original_vint", "i"), V("extended_uint", "i"), V("extended_vint", "i"),
            V("self_int", "u"), V("original_u_start", "c"), V("original_u_end", "c"), V("original_v_start", "c"),
            V("original_v_end", "c"), V("extended_u_start", "c"), V("extended_u_end", "c"), V("extended_v_start", "c"),
            V("extended_v_end", "c"), V("analytic_form_type", "c"), V("swept_form_type", "c"), V("spun_form_type", "c"),
            V("blend_form_type", "c"), P("analytic_form"), P("swept_form"), P("spun_form"), P("blend_form")]),
        // analytic forms of a B-curve / B-surface: the 2006 structs HELIX_CU_FORM_s and HELIX_SU_FORM_s, field for field.
        // DS writes a thread as B-curves and a B-surface with these forms (testdata/helix.rsdocx). Node numbers from that
        // file: 184 follows a CURVE_DATA and has HELIX_CU_FORM's fields (with point, no gap), 163 follows a SURFACE_DATA
        // and has HELIX_SU_FORM's (gap = -3.14158e13, Parasolid's null real); the table read from the 2006 pdf gave them
        // the other way round (a text extraction of that table, columns may have shifted).
        // helix.rsdocx: axis (2.5,-2,32.6) mm, dir (0,0,-1), '+', turns [0,14], pitch 1.5 mm, tol 1e-5.
        [184] = new(184, "HELIX_CU_FORM", [
            V("axis_pt", "v"), V("axis_dir", "v"), V("point", "v"), V("hand", "c"), V("turns", "i"), V("pitch", "f"), V("tol", "f")]),
        [163] = new(163, "HELIX_SU_FORM", [
            V("axis_pt", "v"), V("axis_dir", "v"), V("hand", "c"), V("turns", "i"), V("pitch", "f"), V("gap", "f"), V("tol", "f")]),
        [126] = new(126, "NURBS_SURF", [
            V("u_periodic", "l"), V("v_periodic", "l"), V("u_degree", "n"), V("v_degree", "n"), V("n_u_vertices", "d"),
            V("n_v_vertices", "d"), V("u_knot_type", "u"), V("v_knot_type", "u"), V("n_u_knots", "d"), V("n_v_knots", "d"),
            V("rational", "l"), V("u_closed", "l"), V("v_closed", "l"), V("surface_form", "u"), V("vertex_dim", "n"),
            P("bspline_vertices"), P("u_knot_mult"), P("v_knot_mult"), P("u_knots"), P("v_knots")]),
    };
}
