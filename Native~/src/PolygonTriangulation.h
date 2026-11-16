#pragma once

// Triangulation of one polygon face.
//
// The importer used to draw every face as a fan from its first corner. A fan is exact for a
// convex face and wrong for most concave ones: its triangles reach across the notch, so the face
// comes out covering area it does not have (a U of area 7 imported with area 11). Faces the fan
// cannot cover are triangulated by ear clipping instead; every other face still gets the same fan,
// index for index, so nothing that imported correctly before changes.
//
// No OpenUSD here, on purpose: the functions take plain arrays, so
// Tests~/polygon_triangulation_test.cpp builds and runs them without an OpenUSD install.

#include <cmath>
#include <cstddef>
#include <vector>

namespace rusd
{

// Working storage reused from face to face, and the allowance ear clipping may still spend.
struct PolygonScratch
{
    std::vector<double> plane; // two coordinates per corner: the face laid on its own plane
    std::vector<int> previous;
    std::vector<int> next;
    std::vector<int> triangles; // the caller's buffer for ClipEars output

    // Corner-in-triangle tests left. Ear clipping costs about the square of a face's corner count,
    // and that count comes from the file, so the work is metered: a stage cannot buy unbounded
    // time with one enormous concave face, or with a great many of them. When the allowance runs
    // out the remaining faces are fanned, and ClipEars says so.
    long long allowance = 0;
};

namespace polygon_detail
{

struct Vector
{
    double x;
    double y;
    double z;
};

// From one point to another, widened to double before the subtraction.
inline Vector Between(const float* xyz, int from, int to)
{
    const float* a = xyz + 3 * static_cast<size_t>(from);
    const float* b = xyz + 3 * static_cast<size_t>(to);
    return {static_cast<double>(b[0]) - a[0], static_cast<double>(b[1]) - a[1], static_cast<double>(b[2]) - a[2]};
}

inline Vector Cross(const Vector& a, const Vector& b)
{
    return {a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x};
}

inline double Dot(const Vector& a, const Vector& b)
{
    return a.x * b.x + a.y * b.y + a.z * b.z;
}

// Whether a fan triangle of area vector `area` is folded back against a face of area vector
// `normal`, whose squared length is `size`. Relative to the face's own size, so the answer does
// not depend on the stage's units; the margin keeps a corner that sits on a straight edge, give
// or take float rounding, on the fan path where it always was.
inline bool FoldsBack(const Vector& area, const Vector& normal, double size)
{
    return Dot(area, normal) < -1e-9 * size;
}

} // namespace polygon_detail

// True when the fan from the face's first corner covers the face exactly.
//
// `xyz` holds three floats per point and `corners` the face's `cornerCount` point indices, each
// already checked to be inside `xyz`. When the answer is false, `normal` holds the face's area
// vector (twice its area along its normal) for ClipEars; otherwise it is not meaningful.
//
// The fan's triangles all start at corner 0, and their area vectors add up to the face's. The fan
// is exact when every one of them points the way that sum does: none is folded back across the
// face. A triangle that points against it is the fan cutting across a notch. Convex faces pass,
// and so do concave faces that happen to be visible in full from corner 0, which the fan also
// draws correctly. A face with no area (every corner on a line) has nothing to get wrong.
//
// This runs for every face of four corners or more, so the quad path is kept as short as the
// arithmetic allows. A quad's fan is two triangles; when they do not point against each other,
// neither can point against their sum, and that one comparison settles nearly every quad there
// is, convex or not quite planar. Only a quad whose triangles oppose each other goes on to the
// full test.
inline bool FanIsExact(const float* xyz, const int* corners, int cornerCount, double normal[3])
{
    using namespace polygon_detail;
    if (cornerCount < 4)
    {
        return true;
    }

    Vector sum{0.0, 0.0, 0.0};
    if (cornerCount == 4)
    {
        const Vector diagonal = Between(xyz, corners[0], corners[2]);
        const Vector first = Cross(Between(xyz, corners[0], corners[1]), diagonal);
        const Vector second = Cross(diagonal, Between(xyz, corners[0], corners[3]));
        if (Dot(first, second) >= 0.0)
        {
            return true;
        }

        sum = {first.x + second.x, first.y + second.y, first.z + second.z};
        const double size = Dot(sum, sum);
        if (!(size > 0.0) || !std::isfinite(size))
        {
            return true;
        }

        normal[0] = sum.x;
        normal[1] = sum.y;
        normal[2] = sum.z;
        return !FoldsBack(first, sum, size) && !FoldsBack(second, sum, size);
    }

    // Larger faces are rare (455 of 7.7 million across the conformance stages), so they take the
    // plain route: add the fan's area vectors up, then go round again and compare each with the sum.
    Vector spoke = Between(xyz, corners[0], corners[1]);
    for (int i = 2; i < cornerCount; ++i)
    {
        const Vector nextSpoke = Between(xyz, corners[0], corners[i]);
        const Vector area = Cross(spoke, nextSpoke);
        sum.x += area.x;
        sum.y += area.y;
        sum.z += area.z;
        spoke = nextSpoke;
    }

    const double size = Dot(sum, sum);
    if (!(size > 0.0) || !std::isfinite(size))
    {
        return true;
    }

    normal[0] = sum.x;
    normal[1] = sum.y;
    normal[2] = sum.z;
    spoke = Between(xyz, corners[0], corners[1]);
    for (int i = 2; i < cornerCount; ++i)
    {
        const Vector nextSpoke = Between(xyz, corners[0], corners[i]);
        if (FoldsBack(Cross(spoke, nextSpoke), sum, size))
        {
            return false;
        }

        spoke = nextSpoke;
    }

    return true;
}

// Triangulates a face the fan cannot cover, by ear clipping on the face's own plane.
//
// `normal` is what FanIsExact left for the face. Appends cornerCount - 2 triangles to
// `triangles`, three corner positions each (0 .. cornerCount - 1, positions within the face, not
// point indices), every triangle in the order the face lists its corners; the caller maps them to
// vertices and applies its winding. The count is the same one the fan gives, so buffers sized
// for the fan still fit.
//
// Returns false when it could not finish cleanly: the face crosses itself, so it has no proper
// triangulation, or the allowance ran out. What is left of the face is then closed as a fan, so
// the triangle count still holds; the caller reports it rather than leave the shape changed in
// silence.
inline bool ClipEars(
    const float* xyz,
    const int* corners,
    int cornerCount,
    const double normal[3],
    PolygonScratch* scratch,
    std::vector<int>* triangles)
{
    // Drop the axis the face is most nearly perpendicular to, and keep the remaining two in the
    // order that leaves the face counter-clockwise.
    int drop = 0;
    if (std::fabs(normal[1]) > std::fabs(normal[drop])) drop = 1;
    if (std::fabs(normal[2]) > std::fabs(normal[drop])) drop = 2;
    int u = (drop + 1) % 3;
    int v = (drop + 2) % 3;
    if (normal[drop] < 0.0)
    {
        const int swapped = u;
        u = v;
        v = swapped;
    }

    // The ring of corners still to be clipped, as a doubly linked list over corner positions.
    const size_t count = static_cast<size_t>(cornerCount);
    scratch->plane.resize(count * 2);
    scratch->previous.resize(count);
    scratch->next.resize(count);
    double* const plane = scratch->plane.data();
    int* const previous = scratch->previous.data();
    int* const next = scratch->next.data();
    for (int i = 0; i < cornerCount; ++i)
    {
        const float* point = xyz + 3 * static_cast<size_t>(corners[i]);
        plane[2 * i] = point[u];
        plane[2 * i + 1] = point[v];
        previous[i] = i == 0 ? cornerCount - 1 : i - 1;
        next[i] = i == cornerCount - 1 ? 0 : i + 1;
    }

    // Twice the signed area of triangle a, b, c: positive when it turns the way the face does.
    const auto turn = [plane](int a, int b, int c)
    {
        const double ax = plane[2 * a];
        const double ay = plane[2 * a + 1];
        return (plane[2 * b] - ax) * (plane[2 * c + 1] - ay) - (plane[2 * b + 1] - ay) * (plane[2 * c] - ax);
    };

    const auto samePlace = [plane](int a, int b)
    {
        return plane[2 * a] == plane[2 * b] && plane[2 * a + 1] == plane[2 * b + 1];
    };

    // An ear holds no other corner. One that sits where a, b or c does is the same place listed
    // twice (the seam of a face drawn around a hole) and does not count; one on the cut from a to
    // c does, since cutting there would leave it on an edge.
    const double epsilon = 1e-12 * std::fabs(normal[drop]);
    const auto holdsNoCorner = [&](int a, int b, int c)
    {
        for (int p = next[c]; p != a; p = next[p])
        {
            if (samePlace(p, a) || samePlace(p, b) || samePlace(p, c))
            {
                continue;
            }

            if (turn(a, b, p) > epsilon && turn(b, c, p) > epsilon && turn(c, a, p) >= -epsilon)
            {
                return false;
            }
        }

        return true;
    };

    int remaining = cornerCount;
    int at = 0;
    bool clean = true;
    while (remaining > 3)
    {
        bool clipped = false;
        for (int scanned = 0; scanned < remaining; ++scanned, at = next[at])
        {
            const int a = previous[at];
            const int c = next[at];
            if (!(turn(a, at, c) > epsilon))
            {
                continue; // a notch, or a corner on a straight edge: not an ear
            }

            scratch->allowance -= remaining;
            if (scratch->allowance < 0)
            {
                break;
            }

            if (!holdsNoCorner(a, at, c))
            {
                continue;
            }

            triangles->insert(triangles->end(), {a, at, c});
            next[a] = c;
            previous[c] = a;
            --remaining;
            at = a;
            clipped = true;
            break;
        }

        if (!clipped)
        {
            clean = false;
            break;
        }
    }

    // The last triangle, or whatever could not be clipped, as a fan.
    for (int b = next[at], c = next[b]; c != at; b = c, c = next[c])
    {
        triangles->insert(triangles->end(), {at, b, c});
    }

    return clean;
}

enum class FaceTriangulation
{
    Fan,        // the fan from corner 0 is exact, and is what was appended
    Clipped,    // a concave face, triangulated by ear clipping
    Unresolved, // a concave face ear clipping could not finish; see ClipEars
};

// Both steps together, for callers that have no reason to keep the fan path free of the copy:
// appends the face's cornerCount - 2 triangles as corner positions, in the face's own order.
inline FaceTriangulation TriangulateCorners(
    const float* xyz,
    const int* corners,
    int cornerCount,
    PolygonScratch* scratch,
    std::vector<int>* triangles)
{
    double normal[3] = {0.0, 0.0, 0.0};
    if (FanIsExact(xyz, corners, cornerCount, normal))
    {
        for (int i = 1; i + 1 < cornerCount; ++i)
        {
            triangles->insert(triangles->end(), {0, i, i + 1});
        }

        return FaceTriangulation::Fan;
    }

    return ClipEars(xyz, corners, cornerCount, normal, scratch, triangles) ? FaceTriangulation::Clipped
                                                                           : FaceTriangulation::Unresolved;
}

} // namespace rusd
