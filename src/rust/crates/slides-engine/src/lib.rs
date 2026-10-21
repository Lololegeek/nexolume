use change_tracking::Delta;
use command_system::Command;
use document_core::{Slide, SlideObject};
use serde_json::json;
fn colour(c: &str) -> bool {
    c.len() == 7 && c.starts_with('#') && c[1..].bytes().all(|b| b.is_ascii_hexdigit())
}
/// Slide-level properties (background, notes, transition).
pub fn validate_slide(s: &Slide) -> Result<(), String> {
    if !colour(&s.background)
        || s.notes.len() > 20000
        || !["none", "fade", "slide"].contains(&s.transition.as_str())
    {
        return Err("Invalid slide properties".into());
    }
    Ok(())
}
pub fn validate(o: &SlideObject) -> Result<(), String> {
    if o.id.is_empty()
        || ![
            "text",
            "rectangle",
            "ellipse",
            "rounded",
            "triangle",
            "diamond",
            "line",
            "image",
        ]
        .contains(&o.kind.as_str())
        || (o.kind == "image") != o.image.is_some()
        || o.image.as_ref().is_some_and(|i| {
            !["image/png", "image/jpeg", "image/gif"].contains(&i.mime.as_str())
                || i.data.is_empty()
                || i.data.len() > 12 * 1024 * 1024
                || !i.data.bytes().all(|c| c.is_ascii_alphanumeric() || matches!(c, b'+' | b'/' | b'='))
                || !(i.width.is_finite() && i.height.is_finite() && i.width > 0.0 && i.height > 0.0)
                || i.alt.len() > 500
        })
        || !["left", "center", "right"].contains(&o.align.as_str())
        || !(o.line == "none" || colour(&o.line))
        || !o.line_width.is_finite()
        || !(0.0..=40.0).contains(&o.line_width)
        || !o.rotation.is_finite()
        || !(-360.0..=360.0).contains(&o.rotation)
        || !o.opacity.is_finite()
        || !(0.05..=1.0).contains(&o.opacity)
        || ![o.x, o.y, o.width, o.height].iter().all(|v| v.is_finite())
        || o.x.abs() > 100000.0
        || o.y.abs() > 100000.0
        || !(10.0..=5000.0).contains(&o.width)
        || !(10.0..=5000.0).contains(&o.height)
        || o.text.len() > 100000
        || !o.font_size.is_finite()
        || !(10.0..=160.0).contains(&o.font_size)
        || o.fill.len() != 7
        || !o.fill.starts_with('#')
        || !o.fill[1..].bytes().all(|c| c.is_ascii_hexdigit())
    {
        return Err("Invalid slide object properties".into());
    }
    Ok(())
}
pub fn apply(slides: &mut Vec<Slide>, command: &Command) -> Result<(Command, Delta), String> {
    let p = &command.params;
    let name = command.command.as_str();
    if name == "DuplicateSlide" {
        let id = p["id"].as_str().ok_or("Missing slide id")?;
        let index = slides
            .iter()
            .position(|s| s.id == id)
            .ok_or("Unknown slide")?;
        let mut slide = slides[index].clone();
        slide.id = document_core::id();
        for o in &mut slide.objects {
            o.id = document_core::id();
        }
        return apply(
            slides,
            &Command {
                command: "AddSlide".into(),
                params: json!({"slide":slide,"index":index+1}),
            },
        );
    }
    if name == "MoveSlide" {
        let id = p["id"].as_str().ok_or("Missing slide id")?;
        let from = slides
            .iter()
            .position(|s| s.id == id)
            .ok_or("Unknown slide")?;
        let to = p["index"].as_u64().ok_or("Missing slide index")? as usize;
        if to >= slides.len() {
            return Err("Invalid slide index".into());
        }
        let slide = slides.remove(from);
        slides.insert(to, slide);
        return Ok((
            Command {
                command: "MoveSlide".into(),
                params: json!({"id":id,"index":from}),
            },
            Delta::new("SlideReordered", json!({"id":id,"index":to})),
        ));
    }
    if name == "DuplicateObject" || name == "AlignObject" {
        let slide_id = p["slide"].as_str().ok_or("Missing slide id")?;
        let id = p["id"].as_str().ok_or("Missing object id")?;
        let slide = slides
            .iter()
            .find(|s| s.id == slide_id)
            .ok_or("Unknown slide")?;
        let index = slide
            .objects
            .iter()
            .position(|o| o.id == id)
            .ok_or("Unknown object")?;
        let o = &slide.objects[index];
        if name == "DuplicateObject" {
            let mut object = o.clone();
            object.id = p["new_id"]
                .as_str()
                .map(str::to_string)
                .unwrap_or_else(document_core::id);
            object.x += 24.0;
            object.y += 24.0;
            return apply(
                slides,
                &Command {
                    command: "AddObject".into(),
                    params: json!({"slide":slide_id,"object":object,"index":index+1}),
                },
            );
        }
        let (x, y) = match p["alignment"].as_str().ok_or("Missing alignment")? {
            "left" => (0.0, o.y),
            "center" => ((960.0 - o.width) / 2.0, o.y),
            "right" => (960.0 - o.width, o.y),
            "top" => (o.x, 0.0),
            "middle" => (o.x, (540.0 - o.height) / 2.0),
            "bottom" => (o.x, 540.0 - o.height),
            _ => return Err("Invalid object alignment".into()),
        };
        return apply(
            slides,
            &Command {
                command: "UpdateObject".into(),
                params: json!({"slide":slide_id,"id":id,"x":x,"y":y}),
            },
        );
    }
    if name == "AddSlide" {
        let slide = if let Some(s) = p.get("slide") {
            serde_json::from_value::<Slide>(s.clone()).map_err(|e| e.to_string())?
        } else {
            Slide::new(p["id"].as_str().ok_or("Missing slide id")?.into(), vec![])
        };
        validate_slide(&slide)?;
        if slide.id.is_empty() || slides.iter().any(|s| s.id == slide.id) {
            return Err("Duplicate or empty slide id".into());
        }
        let mut ids: std::collections::HashSet<&str> = slides
            .iter()
            .flat_map(|s| {
                std::iter::once(s.id.as_str()).chain(s.objects.iter().map(|o| o.id.as_str()))
            })
            .collect();
        if !ids.insert(&slide.id) {
            return Err("Duplicate element id".into());
        }
        for o in &slide.objects {
            validate(o)?;
            if !ids.insert(&o.id) {
                return Err("Duplicate element id".into());
            }
        }
        let index = p["index"]
            .as_u64()
            .map(|i| i as usize)
            .unwrap_or(slides.len());
        if index > slides.len() {
            return Err("Invalid slide index".into());
        }
        slides.insert(index, slide.clone());
        return Ok((
            Command {
                command: "DeleteSlide".into(),
                params: json!({"id":slide.id}),
            },
            Delta::new("SlideAdded", json!({"index":index,"slide":slide})),
        ));
    }
    if name == "UpdateSlide" {
        let id = p["id"].as_str().ok_or("Missing slide id")?;
        let slide = slides
            .iter_mut()
            .find(|s| s.id == id)
            .ok_or("Unknown slide")?;
        let mut data = serde_json::to_value(&*slide).unwrap();
        for key in ["background", "notes", "transition"] {
            if let Some(value) = p.get(key) {
                data[key] = value.clone();
            }
        }
        let updated: Slide = serde_json::from_value(data).map_err(|e| e.to_string())?;
        validate_slide(&updated)?;
        let old = std::mem::replace(slide, updated);
        return Ok((
            Command {
                command: "UpdateSlide".into(),
                params: json!({"id":id,"background":old.background,"notes":old.notes,"transition":old.transition}),
            },
            Delta::new(
                "SlideUpdated",
                json!({"id":id,"background":slide.background,"notes":slide.notes,"transition":slide.transition}),
            ),
        ));
    }
    if name == "DeleteSlide" {
        if slides.len() <= 1 {
            return Err("A presentation needs at least one slide".into());
        }
        let id = p["id"].as_str().ok_or("Missing slide id")?;
        let index = slides
            .iter()
            .position(|s| s.id == id)
            .ok_or("Unknown slide")?;
        let slide = slides.remove(index);
        return Ok((
            Command {
                command: "AddSlide".into(),
                params: json!({"index":index,"slide":slide}),
            },
            Delta::new("SlideRemoved", json!({"id":id})),
        ));
    }
    let slide_id = p["slide"].as_str().ok_or("Missing slide id")?;
    if name == "AddObject" {
        let object: SlideObject =
            serde_json::from_value(p["object"].clone()).map_err(|e| e.to_string())?;
        validate(&object)?;
        if slides
            .iter()
            .any(|s| s.id == object.id || s.objects.iter().any(|o| o.id == object.id))
        {
            return Err("Duplicate object id".into());
        }
        let slide = slides
            .iter_mut()
            .find(|s| s.id == slide_id)
            .ok_or("Unknown slide")?;
        let index = p["index"]
            .as_u64()
            .map(|i| i as usize)
            .unwrap_or(slide.objects.len());
        if index > slide.objects.len() {
            return Err("Invalid object index".into());
        }
        slide.objects.insert(index, object.clone());
        return Ok((
            Command {
                command: "DeleteObject".into(),
                params: json!({"slide":slide_id,"id":object.id}),
            },
            Delta::new(
                "ObjectAdded",
                json!({"slide":slide_id,"index":index,"object":object}),
            ),
        ));
    }
    let slide = slides
        .iter_mut()
        .find(|s| s.id == slide_id)
        .ok_or("Unknown slide")?;
    let id = p["id"].as_str().ok_or("Missing object id")?;
    let index = slide
        .objects
        .iter()
        .position(|o| o.id == id)
        .ok_or("Unknown object")?;
    if name == "ReorderObject" {
        let target = p["index"].as_u64().ok_or("Missing object index")? as usize;
        if target >= slide.objects.len() {
            return Err("Invalid layer index".into());
        }
        let object = slide.objects.remove(index);
        slide.objects.insert(target, object);
        return Ok((
            Command {
                command: "ReorderObject".into(),
                params: json!({"slide":slide_id,"id":id,"index":index}),
            },
            Delta::new(
                "ObjectReordered",
                json!({"slide":slide_id,"id":id,"index":target}),
            ),
        ));
    }
    if name == "DeleteObject" {
        let old = slide.objects.remove(index);
        return Ok((
            Command {
                command: "AddObject".into(),
                params: json!({"slide":slide_id,"object":old,"index":index}),
            },
            Delta::new("ObjectRemoved", json!({"slide":slide_id,"id":id})),
        ));
    }
    if ![
        "UpdateObject",
        "MoveObject",
        "ResizeObject",
        "UpdateObjectText",
    ]
    .contains(&name)
    {
        return Err("Unsupported Slides command".into());
    }
    let mut data = serde_json::to_value(&slide.objects[index]).unwrap();
    for key in [
        "x",
        "y",
        "width",
        "height",
        "text",
        "fill",
        "font_size",
        "bold",
        "italic",
        "underline",
        "align",
        "line",
        "line_width",
        "rotation",
        "opacity",
    ] {
        if let Some(value) = p.get(key) {
            data[key] = value.clone();
        }
    }
    let object: SlideObject = serde_json::from_value(data).map_err(|e| e.to_string())?;
    validate(&object)?;
    let old = std::mem::replace(&mut slide.objects[index], object.clone());
    let mut inverse = serde_json::to_value(old).unwrap();
    inverse["slide"] = json!(slide_id);
    Ok((
        Command {
            command: "UpdateObject".into(),
            params: inverse,
        },
        Delta::new(
            if name == "MoveObject" {
                "ObjectMoved"
            } else {
                "ObjectUpdated"
            },
            json!({"slide":slide_id,"object":object}),
        ),
    ))
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn image_objects_need_valid_picture_data() {
        let mut slides = vec![Slide::new("s".into(), vec![])];
        let object = |kind: &str, image: serde_json::Value| Command {
            command: "AddObject".into(),
            params: json!({"slide":"s","object":{"id":"pic","kind":kind,"text":"","x":10,"y":20,"width":200,"height":100,"fill":"#FFFFFF","image":image}}),
        };
        let good = json!({"mime":"image/png","data":"iVBORw0KGgo=","width":4,"height":2,"alt":"logo"});
        assert!(apply(&mut slides, &object("image", json!(null))).is_err());
        assert!(apply(&mut slides, &object("rectangle", good.clone())).is_err());
        assert!(apply(&mut slides, &object("image", json!({"mime":"text/html","data":"AA==","width":4,"height":2}))).is_err());
        let (inverse, _) = apply(&mut slides, &object("image", good)).unwrap();
        assert_eq!(slides[0].objects[0].image.as_ref().unwrap().alt, "logo");
        apply(&mut slides, &inverse).unwrap();
        assert!(slides[0].objects.is_empty());
    }
    #[test]
    fn objects_move_resize_and_inverse() {
        let mut slides = vec![Slide::new("s".into(), vec![])];
        let c = Command {
            command: "AddObject".into(),
            params: json!({"slide":"s","object":{"id":"rect","kind":"rectangle","text":"","x":10,"y":20,"width":200,"height":100,"fill":"#477BEF"}}),
        };
        apply(&mut slides, &c).unwrap();
        let (inverse, d) = apply(
            &mut slides,
            &Command {
                command: "MoveObject".into(),
                params: json!({"slide":"s","id":"rect","x":100,"y":90}),
            },
        )
        .unwrap();
        assert_eq!(d.kind, "ObjectMoved");
        assert_eq!(slides[0].objects[0].x, 100.0);
        apply(&mut slides, &inverse).unwrap();
        assert_eq!(slides[0].objects[0].x, 10.0);
        assert!(apply(
            &mut slides,
            &Command {
                command: "ResizeObject".into(),
                params: json!({"slide":"s","id":"rect","width":-1})
            }
        )
        .is_err());
        assert_eq!(slides[0].objects[0].width, 200.0);
    }
    #[test]
    fn shape_styles_and_slide_properties() {
        let mut slides = vec![Slide::new("s".into(), vec![])];
        for kind in ["rounded", "triangle", "diamond", "line"] {
            apply(
                &mut slides,
                &Command {
                    command: "AddObject".into(),
                    params: json!({"slide":"s","object":{"id":kind,"kind":kind,"text":"","x":10,"y":20,"width":200,"height":100,"fill":"#477BEF"}}),
                },
            )
            .unwrap();
        }
        let update = |slides: &mut Vec<Slide>, params: serde_json::Value| {
            apply(
                slides,
                &Command {
                    command: "UpdateObject".into(),
                    params,
                },
            )
        };
        let (inverse, _) = update(
            &mut slides,
            json!({"slide":"s","id":"rounded","rotation":45,"opacity":0.5,"line":"#112233","line_width":4,"italic":true,"underline":true,"align":"center"}),
        )
        .unwrap();
        let o = &slides[0].objects[0];
        assert!(o.italic && o.underline && o.align == "center" && o.rotation == 45.0);
        apply(&mut slides, &inverse).unwrap();
        assert_eq!(slides[0].objects[0].rotation, 0.0);
        for bad in [
            json!({"rotation":400}),
            json!({"opacity":0}),
            json!({"line":"red"}),
            json!({"align":"justify"}),
        ] {
            let mut params = bad.clone();
            params["slide"] = json!("s");
            params["id"] = json!("rounded");
            assert!(update(&mut slides, params).is_err());
        }
        let (undo, delta) = apply(
            &mut slides,
            &Command {
                command: "UpdateSlide".into(),
                params: json!({"id":"s","background":"#001122","notes":"Dire bonjour","transition":"fade"}),
            },
        )
        .unwrap();
        assert_eq!(delta.kind, "SlideUpdated");
        assert_eq!(slides[0].notes, "Dire bonjour");
        apply(&mut slides, &undo).unwrap();
        assert_eq!(slides[0].background, "#FFFFFF");
        assert!(apply(
            &mut slides,
            &Command {
                command: "UpdateSlide".into(),
                params: json!({"id":"s","transition":"spin"})
            }
        )
        .is_err());
    }
}
