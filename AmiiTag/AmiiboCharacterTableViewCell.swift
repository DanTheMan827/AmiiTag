//
//  AmiiboCharacterTableViewCell.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 7/13/20.
//  Copyright © 2020 Daniel Radtke. All rights reserved.
//

import Foundation
import UIKit
import MobileCoreServices

class AmiiboCharacterTableViewCell: UITableViewCell {
    @IBOutlet var CellImage: UIImageView!
    @IBOutlet var CellLabel: UILabel!
}

class AmiiboCharacterPuckTableViewCell: UITableViewCell, LibraryPickerProtocol, UIDocumentPickerDelegate {
    func AmiiboSeriesPicked(series: String) -> Bool {
        return true
    }
    
    func AmiiboCharacterPicked(tag: TagDump) -> Bool {
        let alert = UIAlertController(title: "Please Wait", message: "Writing " + (Puck.name), preferredStyle: .alert)
        if self.dismiss {
            self.ViewController.dismiss(animated: true)
        }
        dismiss = true
        self.ViewController.present(alert, animated: true)
        
        Puck.writeTag(toSlot: Info.slot, using: tag.data) { (result) in
            switch result {
            case .success(()):
                self.ViewController.dismiss(animated: true)
                
                let alert = UIAlertController(title: "Please Wait", message: "Changing Slot", preferredStyle: .alert)
                self.ViewController.present(alert, animated: true, completion: nil)
                
                
                self.ViewController.cells[Int(self.Info.slot)].CellImage.image = tag.image
                self.ViewController.cells[Int(self.Info.slot)].CellLabel.text = tag.displayName
                self.Puck.getSlotSummary { (result) in
                    switch result {
                    case .success(let summary):
                        if summary.current == self.Info.slot {
                            self.Puck.changeSlot { (result) in
                                self.ViewController.dismiss(animated: true)
                                switch result {
                                case .success(()):
                                    break
                                case .failure(let error):
                                    let errorAlert = UIAlertController(title: "Oh no!", message: error.localizedDescription, preferredStyle: .alert)
                                    errorAlert.addAction(UIAlertAction(title: "OK", style: .default, handler: nil))
                                    self.ViewController.present(errorAlert, animated: true)
                                    break
                                }
                            }
                        } else {
                            self.ViewController.dismiss(animated: true)
                        }
                        
                        break
                    case .failure(let error):
                        let errorAlert = UIAlertController(title: "Oh no!", message: error.localizedDescription, preferredStyle: .alert)
                        errorAlert.addAction(UIAlertAction(title: "OK", style: .default, handler: nil))
                        self.ViewController.present(errorAlert, animated: true)
                        break
                    }
                }
                break
            case .failure(let error):
                self.ViewController.dismiss(animated: true)
                let errorAlert = UIAlertController(title: "Oh no!", message: error.localizedDescription, preferredStyle: .alert)
                errorAlert.addAction(UIAlertAction(title: "OK", style: .default, handler: nil))
                self.ViewController.present(errorAlert, animated: true)
                break
            }
        }
        return false
    }
    
    var Info: PuckPeripheral.SlotInfo!
    var Puck: PuckPeripheral!
    var ViewController: AmiiboCharactersPuckTableViewController!
    fileprivate var dismiss = true
    @IBOutlet var CellImage: UIImageView!
    @IBOutlet var CellLabel: UILabel!
    @IBAction func downloadTapped(_ sender: Any) {
        let alert = UIAlertController(title: "Please Wait", message: "Reading " + (Puck.name), preferredStyle: .alert)
        self.ViewController.present(alert, animated: true)
        
        Puck.readTag(slot: Info.slot) { (result) in
            switch result {
            case .success(let tag):
                self.ViewController.dismiss(animated: true)
                TagInfoViewController.openTagInfo(dump: TagDump(data: tag)!, controller: self.ViewController)
                break
            case .failure(let error):
                self.ViewController.dismiss(animated: true)
                let errorAlert = UIAlertController(title: "Oh no!", message: error.localizedDescription, preferredStyle: .alert)
                errorAlert.addAction(UIAlertAction(title: "OK", style: .default, handler: nil))
                self.ViewController.present(errorAlert, animated: true)
                break
            }
        }
    }
    
    @IBAction func uploadTapped(_ sender: Any) {
        let alertController = UIAlertController(title: nil, message: nil, preferredStyle: .actionSheet)
        
        alertController.addAction(UIAlertAction(title: "Amiibo Library", style: .default, handler: { (action) in
            LibraryPicker.ShowPicker(using: self.ViewController, with: self)
        }))
        
        alertController.addAction(UIAlertAction(title: "Load Tag", style: .default, handler: { (action) in
            var pickerController = UIDocumentPickerViewController(documentTypes: [kUTTypeData as String], in: .open)
            pickerController.delegate = self
            pickerController.allowsMultipleSelection = false
            
            self.ViewController.present(pickerController, animated: true, completion: nil)
        }))
        
        alertController.addAction(UIAlertAction(title: "Cancel", style: .cancel, handler: nil))
        
        self.ViewController.present(alertController, animated: true, completion: nil)
    }
    
    // MARK: DocumentDelegate
    public func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
        guard let url = urls.first else {
            return
        }
        if url.isFileURL {
            let shouldStopAccessing = url.startAccessingSecurityScopedResource()
            let path = url.path
            if let attr = try? FileManager.default.attributesOfItem(atPath: path) as NSDictionary {
                if attr.fileSize() == 532 || attr.fileSize() == 540 || attr.fileSize() == 572 {
                    if let data = try? Data(contentsOf: url),
                       let dump = TagDump(data: data) {
                        self.dismiss = false
                        self.AmiiboCharacterPicked(tag: dump)
                    }
                    
                }
            }
            if shouldStopAccessing {
                url.stopAccessingSecurityScopedResource()
            }
        }
    }
}
