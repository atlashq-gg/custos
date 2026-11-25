//! A parser of the Server-Sent Events format of the HTML standard, for the stream of
//! spec/05-stream.md. Give it the bytes of the response as they arrive. It gives the complete events.

/// The longest line that the parser keeps. It drops the bytes after this limit, so a broken service
/// cannot fill the memory. A message has 500 characters of text at most, so its line is much shorter.
const MAX_LINE: usize = 64 * 1024;

/// An event: the fields `id` and `data`. The parser ignores the other fields.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Event {
    pub id: Option<String>,
    /// The `data` lines, joined with a line break.
    pub data: Option<String>,
}

#[derive(Debug, Default)]
pub struct Parser {
    /// The bytes of the line that is not complete.
    line: Vec<u8>,
    /// True after the first line, which can start with a byte order mark.
    started: bool,
    /// True when the last line ended with CR. A LF after it is part of the same line end.
    after_cr: bool,
    event: Event,
}

impl Parser {
    /// Reads the bytes, and gives the events that they complete.
    pub fn feed(&mut self, bytes: &[u8]) -> Vec<Event> {
        let mut events = Vec::new();
        for &byte in bytes {
            if std::mem::take(&mut self.after_cr) && byte == b'\n' {
                continue;
            }
            if byte == b'\n' || byte == b'\r' {
                self.after_cr = byte == b'\r';
                let line = std::mem::take(&mut self.line);
                events.extend(self.read_line(&line));
            } else if self.line.len() < MAX_LINE {
                self.line.push(byte);
            }
        }
        events
    }

    fn read_line(&mut self, bytes: &[u8]) -> Option<Event> {
        let text = String::from_utf8_lossy(bytes);
        let mut line = text.as_ref();
        if !std::mem::replace(&mut self.started, true) {
            line = line.strip_prefix('\u{feff}').unwrap_or(line);
        }

        if line.is_empty() {
            let event = std::mem::take(&mut self.event);
            return (event.id.is_some() || event.data.is_some()).then_some(event);
        }
        let (field, value) = line.split_once(':').unwrap_or((line, ""));
        let value = value.strip_prefix(' ').unwrap_or(value);
        match field {
            // A line that starts with ":" is a comment. Its field name is empty.
            "id" if !value.contains('\0') => self.event.id = Some(value.to_owned()),
            "data" => match &mut self.event.data {
                Some(data) => {
                    data.push('\n');
                    data.push_str(value);
                }
                None => self.event.data = Some(value.to_owned()),
            },
            _ => {}
        }
        None
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn event(id: Option<&str>, data: Option<&str>) -> Event {
        Event {
            id: id.map(str::to_owned),
            data: data.map(str::to_owned),
        }
    }

    #[test]
    fn position_comment_and_message_of_the_spec() {
        let stream = "id: 1-141\n\nid: 1-142\ndata: {\"kind\":\"event\"}\n\n: keep-alive\n\n";
        let events = Parser::default().feed(stream.as_bytes());

        assert_eq!(
            events,
            [
                event(Some("1-141"), None),
                event(Some("1-142"), Some("{\"kind\":\"event\"}"))
            ]
        );
    }

    #[test]
    fn each_line_end_is_accepted() {
        for stream in ["id: 1\r\ndata: a\r\n\r\n", "id: 1\rdata: a\r\r", "id: 1\ndata: a\n\n"] {
            assert_eq!(
                Parser::default().feed(stream.as_bytes()),
                [event(Some("1"), Some("a"))],
                "{stream:?}"
            );
        }
    }

    #[test]
    fn line_end_and_text_can_split_across_chunks() {
        let mut parser = Parser::default();
        assert!(parser.feed(b"id: 1\r").is_empty());
        assert!(parser.feed(b"\nda").is_empty());
        assert!(parser.feed(b"ta: a\r").is_empty());
        assert_eq!(parser.feed(b"\n\r\n"), [event(Some("1"), Some("a"))]);
    }

    #[test]
    fn byte_order_mark_unknown_fields_and_more_data_lines() {
        let stream = "\u{feff}id:7\nevent: x\nretry: 5\ndata: a\ndata:b\nunknown\n\n";

        assert_eq!(
            Parser::default().feed(stream.as_bytes()),
            [event(Some("7"), Some("a\nb"))]
        );
    }

    #[test]
    fn id_with_a_null_is_ignored_and_an_empty_event_gives_nothing() {
        let events = Parser::default().feed(b"id: a\0b\ndata: x\n\n\n\n");

        assert_eq!(events, [event(None, Some("x"))]);
    }

    #[test]
    fn long_line_is_cut() {
        let long = format!("data: {}\n\n", "a".repeat(MAX_LINE * 2));
        let events = Parser::default().feed(long.as_bytes());

        assert_eq!(
            events[0].data.as_ref().map(String::len),
            Some(MAX_LINE - "data: ".len())
        );
    }
}
