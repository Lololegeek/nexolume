use command_system::Command;
#[derive(Default)]
pub struct History {
    pub undo: Vec<Entry>,
    pub redo: Vec<Entry>,
}
pub struct Entry {
    pub forward: Vec<Command>,
    pub inverse: Vec<Command>,
    pub group: Option<String>,
}
impl History {
    pub fn record(
        &mut self,
        forward: Vec<Command>,
        mut inverse: Vec<Command>,
        group: Option<String>,
    ) {
        inverse.reverse();
        if let Some(previous) = self.undo.last_mut() {
            if group.is_some() && previous.group == group {
                if forward.len() == 1
                    && inverse.len() == 1
                    && previous.forward.len() == 1
                    && previous.inverse.len() == 1
                    && forward[0].command == "SetParagraph"
                    && previous.forward[0].command == "SetParagraph"
                    && forward[0].params["id"] == previous.forward[0].params["id"]
                {
                    previous.forward = forward;
                    self.redo.clear();
                    return;
                }
                previous.forward.extend(forward);
                inverse.append(&mut previous.inverse);
                previous.inverse = inverse;
                self.redo.clear();
                return;
            }
        }
        self.undo.push(Entry {
            forward,
            inverse,
            group,
        });
        self.redo.clear();
    }
}
