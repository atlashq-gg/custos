//! The engine of Custos. It has no GUI dependency, so its tests run without a desktop.
//!
//! The application crate starts an [`agent::Agent`], sends it [`agent::Command`]s, and shows the
//! [`agent::Event`]s that it receives.

pub mod agent;
pub mod auth;
pub mod config;
pub mod discovery;
pub mod locate;
pub mod process;
pub mod schedule;
#[cfg(target_os = "linux")]
pub mod screen;
pub mod stream;
pub mod upload;
pub mod url_rules;
pub mod watch;
